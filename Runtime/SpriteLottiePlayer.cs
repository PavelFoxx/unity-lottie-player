using System.Collections;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Gilzoide.LottiePlayer
{
    /// <summary>
    /// Plays a Lottie animation outside of a Canvas, rendering it to a
    /// <see cref="Texture2D"/> that is displayed through a <see cref="SpriteRenderer"/>.
    /// </summary>
    /// <remarks>
    /// This is the world-space / 2D counterpart of <see cref="UI.ImageLottiePlayer"/>.
    /// It mirrors the same playback logic (coroutine driven, Burst render jobs), but
    /// instead of drawing into a CanvasRenderer mesh it creates a <see cref="Sprite"/>
    /// backed by the rendered texture and assigns it to the attached SpriteRenderer.
    /// </remarks>
    [RequireComponent(typeof(SpriteRenderer))]
    [AddComponentMenu("Lottie/Sprite Lottie Player")]
    public class SpriteLottiePlayer : MonoBehaviour
    {
        [Header("Animation Options")]
        [SerializeField] protected LottieAnimationAsset _animationAsset;
        [SerializeField] protected AutoPlayEvent _autoPlay = AutoPlayEvent.OnStart;
        [SerializeField] protected bool _loop = true;

        [Header("Texture Options")]
        [SerializeField, Min(2)] protected int _width = 128;
        [SerializeField, Min(2)] protected int _height = 128;
        [Tooltip("If enabled, the animation keeps its aspect ratio inside the texture, " +
            "letterboxing with transparent borders when the texture aspect differs. " +
            "If disabled, the animation is stretched to fill the whole texture.")]
        [SerializeField] protected bool _keepAspect = true;
        [Tooltip("rlottie renders top-down while Unity textures are stored bottom-up, " +
            "so the raw result appears upside down on a Sprite. Keep this enabled to flip " +
            "the texture vertically so the sprite is displayed the right way up.")]
        [SerializeField] protected bool _flipVertical = true;

        [Header("Sprite Options")]
        [Tooltip("How many texture pixels correspond to one world unit. Lower values make the sprite bigger.")]
        [SerializeField, Min(0.01f)] protected float _pixelsPerUnit = 100f;
        [Tooltip("Normalized pivot of the generated sprite (0,0 = bottom-left, 0.5,0.5 = center, 1,1 = top-right).")]
        [SerializeField] protected Vector2 _pivot = new Vector2(0.5f, 0.5f);

        protected SpriteRenderer _spriteRenderer;
        protected Texture2D _texture;
        protected Sprite _sprite;
        protected NativeLottieAnimation _animation;
        protected float _time = 0;
        protected uint _currentFrame = 0;
        protected uint _lastRenderedFrame = 0;
        protected JobHandle _renderJobHandle;
        protected Coroutine _playCoroutine;
        private string _lastAnimationAssetCacheKey;

        /// <summary>The SpriteRenderer this player draws into.</summary>
        public SpriteRenderer SpriteRenderer
        {
            get
            {
                if (_spriteRenderer == null)
                {
                    _spriteRenderer = GetComponent<SpriteRenderer>();
                }
                return _spriteRenderer;
            }
        }

        /// <summary>Texture the animation is rendered into. Recreated when size changes.</summary>
        public Texture2D Texture => _texture;

        public bool IsPlaying => _playCoroutine != null;

        protected virtual void OnEnable()
        {
            RecreateAnimationIfNeeded();
            if (_autoPlay == AutoPlayEvent.OnEnable && Application.isPlaying)
            {
                Play();
            }
        }

        protected virtual void Start()
        {
            if (_autoPlay == AutoPlayEvent.OnStart && Application.isPlaying)
            {
                Play();
            }
        }

        protected virtual void OnDisable()
        {
            Pause();
        }

        protected virtual void OnDestroy()
        {
            DiscardRenderJob();
            DestroySprite();
            DestroyTexture();
            _animation.Dispose();
        }

        public void SetAnimationAsset(LottieAnimationAsset animationAsset)
        {
            if (_animationAsset == animationAsset)
            {
                return;
            }
            Pause();
            _animationAsset = animationAsset;
            RecreateAnimationIfNeeded();
        }

        public void SetAnimation(NativeLottieAnimation animation)
        {
            if (_animation == animation)
            {
                return;
            }
            Pause();
            _animationAsset = null;
            RecreateAnimationIfNeeded(animation);
        }

        [ContextMenu("Play")]
        public void Play()
        {
            Play(0);
        }

        public void Play(float startTime)
        {
            Pause();
            _time = startTime;
            Unpause();
        }

        [ContextMenu("Pause")]
        public void Pause()
        {
            if (_playCoroutine != null)
            {
                StopCoroutine(_playCoroutine);
                _playCoroutine = null;
            }
        }

        [ContextMenu("Unpause")]
        public void Unpause()
        {
            if (!IsPlaying && _animation.IsValid() && isActiveAndEnabled)
            {
                _playCoroutine = StartCoroutine(PlayRoutine());
            }
        }

        protected IEnumerator PlayRoutine()
        {
            // force render first frame
            _lastRenderedFrame = uint.MaxValue;

            float duration = (float) _animation.GetDuration();
            while (_loop || _time < duration)
            {
                _currentFrame = _animation.GetFrameAtTime(_time, _loop);
                if (_currentFrame != _lastRenderedFrame)
                {
                    ScheduleRenderJob(_currentFrame);
                }
                yield return null;
                _time += Time.deltaTime;
                if (_currentFrame != _lastRenderedFrame)
                {
                    CompleteRenderJob();
                }
            }
            CompleteRenderJob();
            _playCoroutine = null;
        }

        protected void RecreateAnimationIfNeeded()
        {
            if (_animationAsset != null && _animationAsset.CacheKey == _lastAnimationAssetCacheKey)
            {
                return;
            }

            if (_animationAsset != null)
            {
                _lastAnimationAssetCacheKey = _animationAsset.CacheKey;
                RecreateAnimationIfNeeded(_animationAsset.CreateNativeAnimation());
            }
            else
            {
                _lastAnimationAssetCacheKey = null;
                RecreateAnimationIfNeeded(NativeLottieAnimation.Invalid);
            }
        }

        protected void RecreateAnimationIfNeeded(NativeLottieAnimation newAnimation)
        {
            if (_animation.IsValid())
            {
                DiscardRenderJob();
                _animation.Dispose();
            }

            if (!newAnimation.IsValid())
            {
                DestroySprite();
                DestroyTexture();
                SpriteRenderer.sprite = null;
                return;
            }

            _animation = newAnimation;
            if (_texture == null
                || _width != _texture.width
                || _height != _texture.height)
            {
                DestroyTexture();
                _texture = _animation.CreateTexture(_width, _height, false);
            }
            CreateOrUpdateSprite();

            if (!Application.isPlaying)
            {
                RenderNow();
            }
        }

        /// <summary>
        /// Creates the Sprite that wraps <see cref="_texture"/> (or recreates it when the
        /// texture, pivot or pixels-per-unit changed) and assigns it to the SpriteRenderer.
        /// </summary>
        protected void CreateOrUpdateSprite()
        {
            if (_texture == null)
            {
                return;
            }

            bool needsNewSprite = _sprite == null
                || _sprite.texture != _texture
                || _sprite.pixelsPerUnit != _pixelsPerUnit
                || _sprite.pivot != new Vector2(_pivot.x * _texture.width, _pivot.y * _texture.height);

            if (needsNewSprite)
            {
                DestroySprite();
                _sprite = Sprite.Create(
                    _texture,
                    new Rect(0, 0, _texture.width, _texture.height),
                    _pivot,
                    Mathf.Max(0.01f, _pixelsPerUnit)
                );
                _sprite.name = "LottieSprite";
            }
            SpriteRenderer.sprite = _sprite;
        }

        protected void RenderNow()
        {
            if (!_animation.IsValid() || _texture == null)
            {
                return;
            }
            DiscardRenderJob();
            // Reuse the job pipeline (render + optional vertical flip) and complete it
            // synchronously, so the editor preview matches the played animation.
            ScheduleRenderJob(_currentFrame);
            _renderJobHandle.Complete();
            _texture.Apply(false);
        }

        protected void ScheduleRenderJob(uint frame)
        {
            if (_flipVertical)
            {
                // Share a single NativeArray instance between both jobs and chain them by
                // dependency, which the Job safety system allows for the same container.
                NativeArray<Color32> buffer = _texture.GetRawTextureData<Color32>();
                int width = _texture.width;
                int height = _texture.height;
                JobHandle renderHandle = _animation
                    .CreateRenderJob(frame, (uint) width, (uint) height, buffer, _keepAspect)
                    .Schedule();
                _renderJobHandle = new FlipVerticalJob
                {
                    Buffer = buffer,
                    Width = width,
                    Height = height,
                }.Schedule(renderHandle);
            }
            else
            {
                _renderJobHandle = _animation.CreateRenderJob(frame, _texture, keepAspectRatio: _keepAspect).Schedule();
            }
        }

        protected void CompleteRenderJob()
        {
            _lastRenderedFrame = _currentFrame;
            _renderJobHandle.Complete();
            _texture.Apply(false);
        }

        protected void DiscardRenderJob()
        {
            _renderJobHandle.Complete();
        }

        protected void DestroyTexture()
        {
            if (_texture != null)
            {
                DestroyImmediate(_texture);
                _texture = null;
            }
        }

        protected void DestroySprite()
        {
            if (_sprite != null)
            {
                DestroyImmediate(_sprite);
                _sprite = null;
            }
        }

        /// <summary>
        /// Flips the rendered pixel buffer vertically (in place), so the top-down image
        /// produced by rlottie matches Unity's bottom-up texture layout.
        /// </summary>
        [BurstCompile]
        protected struct FlipVerticalJob : IJob
        {
            public NativeArray<Color32> Buffer;
            public int Width;
            public int Height;

            public void Execute()
            {
                int half = Height / 2;
                for (int y = 0; y < half; y++)
                {
                    int topRow = y * Width;
                    int bottomRow = (Height - 1 - y) * Width;
                    for (int x = 0; x < Width; x++)
                    {
                        Color32 tmp = Buffer[topRow + x];
                        Buffer[topRow + x] = Buffer[bottomRow + x];
                        Buffer[bottomRow + x] = tmp;
                    }
                }
            }
        }

#if UNITY_EDITOR
        protected virtual void OnValidate()
        {
            if (!isActiveAndEnabled)
            {
                return;
            }
            // Defer the refresh: DestroyImmediate (used while recreating the texture/sprite)
            // must not be called directly from OnValidate.
            UnityEditor.EditorApplication.delayCall += RefreshInEditor;
        }

        private void RefreshInEditor()
        {
            UnityEditor.EditorApplication.delayCall -= RefreshInEditor;
            // `this == null` catches objects that were destroyed before the deferred call ran.
            if (this == null || !isActiveAndEnabled || Application.isPlaying)
            {
                return;
            }
            // Force a refresh so size/aspect/pivot/pixelsPerUnit changes re-apply.
            _lastAnimationAssetCacheKey = null;
            RecreateAnimationIfNeeded();
        }
#endif
    }
}
