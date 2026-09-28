using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;
using Object = UnityEngine.Object;

namespace LCReplay.Plugin.UI
{
    /// <summary>Independent game-native controls styled after LethalConfig's solid red menu.</summary>
    public sealed class NativeReplayUi : IDisposable
    {
        public static readonly Color Orange = new Color(1f, 0.52f, 0.13f, 1f);
        public static readonly Color Black = new Color(0.18f, 0.005f, 0.005f, 1f);
        public static readonly Color Gray = new Color(0.29f, 0.012f, 0.01f, 1f);
        public static readonly Color White = new Color(1f, 0.86f, 0.64f, 1f);
        public static readonly Color ButtonColor = new Color(0.42f, 0.025f, 0.015f, 1f);
        public static readonly Color HeaderColor = new Color(0.34f, 0.01f, 0.008f, 1f);
        private readonly Dictionary<Behaviour, bool> _suspended = new Dictionary<Behaviour, bool>();
        private readonly Dictionary<Selectable, bool> _selectableStates = new Dictionary<Selectable, bool>();
        private readonly List<Object> _ownedAssets = new List<Object>();
        private readonly bool _modal;
        private readonly CursorLockMode _cursorLock;
        private readonly bool _cursorVisible;
        private EventSystem? _eventSystem;
        private EventSystem? _previousEventSystem;
        private GameObject? _previousSelection;
        private TMP_FontAsset? _font;
        private TMP_FontAsset? _fallbackFont;
        private readonly Dictionary<TextMeshProUGUI, string> _textValues = new Dictionary<TextMeshProUGUI, string>();
        private readonly HashSet<uint> _fontCharacters = new HashSet<uint>();
        private readonly Dictionary<uint, Glyph> _fontGlyphs = new Dictionary<uint, Glyph>();
        private readonly List<GlyphRect> _freeGlyphRects = new List<GlyphRect>();
        private readonly List<GlyphRect> _usedGlyphRects = new List<GlyphRect>();
        private string _fontFile = "";
        private MethodInfo? _addGlyph;
        private Texture2D? _fontAtlas;
        private float _nextScan;
        private bool _disposed;
        public GameObject Root { get; private set; } = null!;
        public RectTransform Rect => (RectTransform)Root.transform;

        public NativeReplayUi(string name, int sortingOrder = 32000, bool modal = true)
        {
            _modal = modal;
            _cursorLock = Cursor.lockState;
            _cursorVisible = Cursor.visible;
            _previousEventSystem = EventSystem.current;
            _previousSelection = _previousEventSystem != null ? _previousEventSystem.currentSelectedGameObject : null;
            try
            {
                Root = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                Root.hideFlags = HideFlags.DontSave;
                Object.DontDestroyOnLoad(Root);
                var canvas = Root.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = sortingOrder;
                canvas.pixelPerfect = false;
                var scaler = Root.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
                FindFont();
                if (modal) SuspendOtherInput();
                SuspendOtherSelectables();
                var dispatcher = NewRect(Root.transform, "Replay EventSystem").gameObject;
                _eventSystem = dispatcher.AddComponent<EventSystem>();
                var inputType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule", false)).FirstOrDefault(type => type != null);
                if (inputType == null) throw new InvalidOperationException("The game's Input System UI module is unavailable.");
                var input = dispatcher.AddComponent(inputType);
                // Newer Input System versions assign actions in OnEnable. Replacing
                // those actions again needlessly allocates another default asset.
                if (inputType.GetProperty("actionsAsset", BindingFlags.Public | BindingFlags.Instance)?.GetValue(input, null) == null)
                    inputType.GetMethod("AssignDefaultActions", BindingFlags.Public | BindingFlags.Instance)?.Invoke(input, null);
                if (!modal)
                {
                    // Playback has its own Space/arrow shortcuts. UI navigation and
                    // submit must not run a second action for the same key press.
                    inputType.GetProperty("move", BindingFlags.Public | BindingFlags.Instance)?.SetValue(input, null, null);
                    inputType.GetProperty("submit", BindingFlags.Public | BindingFlags.Instance)?.SetValue(input, null, null);
                }
                EventSystem.current = _eventSystem;
                if (modal)
                {
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
            }
            catch { Dispose(); throw; }
        }

        public bool Owns(Component component) => Root != null && component != null && (component.gameObject == Root || component.transform.IsChildOf(Root.transform));

        public void Tick()
        {
            if (_disposed) return;
            var fontChanged = false;
            foreach (var text in _textValues.Keys.ToArray())
            {
                if (text == null) { _textValues.Remove(text!); continue; }
                if (_textValues[text] == text.text) continue;
                fontChanged |= EnsureGlyphs(text.text);
                _textValues[text] = text.text;
            }
            if (fontChanged)
                foreach (var text in _textValues.Keys)
                    if (text != null) text.ForceMeshUpdate(false, true);
            if (_modal) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            if (Time.realtimeSinceStartup < _nextScan) return;
            _nextScan = Time.realtimeSinceStartup + 0.5f;
            if (_modal) SuspendOtherInput();
            SuspendOtherSelectables();
            if (_eventSystem != null) EventSystem.current = _eventSystem;
        }

        public Image CreatePanel(Transform parent, string name, Color color)
        {
            var image = NewRect(parent, name).gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = true;
            return image;
        }

        public TextMeshProUGUI CreateText(Transform parent, string value, float size = 21, Color? color = null, TextAlignmentOptions alignment = TextAlignmentOptions.TopLeft)
        {
            EnsureGlyphs(value);
            var text = NewRect(parent, "Label").gameObject.AddComponent<TextMeshProUGUI>();
            if (_font != null) text.font = _font;
            text.text = value;
            text.fontSize = size;
            text.color = color ?? Orange;
            text.alignment = alignment;
            text.richText = false;
            text.raycastTarget = false;
            text.enableWordWrapping = true;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.margin = Vector4.zero;
            _textValues[text] = value;
            return text;
        }

        public Button CreateButton(Transform parent, string label, Action click, bool filled = false)
        {
            var panel = CreatePanel(parent, "Button " + label, filled ? Orange : ButtonColor);
            var button = panel.gameObject.AddComponent<Button>();
            button.targetGraphic = panel;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.25f, 1.13f, 1.07f);
            colors.pressedColor = new Color(0.73f, 0.63f, 0.59f);
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(0.55f, 0.55f, 0.55f, 1f);
            colors.colorMultiplier = 1;
            colors.fadeDuration = 0.12f;
            button.colors = colors;
            var text = CreateText(button.transform, label, 19, filled ? White : Orange, TextAlignmentOptions.MidlineLeft);
            SetRect(text.rectTransform, Vector2.zero, Vector2.one, new Vector2(9, 2), new Vector2(-9, -2));
            button.onClick.AddListener(() => click());
            var navigation = button.navigation;
            navigation.mode = Navigation.Mode.Automatic;
            button.navigation = navigation;
            return button;
        }

        public ScrollRect CreateScroll(Transform parent, string name, out RectTransform content)
        {
            var outer = NewRect(parent, name);
            var scroll = outer.gameObject.AddComponent<ScrollRect>();
            var viewport = CreatePanel(outer, "Viewport", new Color(0, 0, 0, 0));
            SetRect(viewport.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(-19, 0));
            viewport.gameObject.AddComponent<RectMask2D>();
            content = NewRect(viewport.transform, "Content");
            content.anchorMin = new Vector2(0, 1);
            content.anchorMax = Vector2.one;
            content.pivot = new Vector2(0.5f, 1);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;
            scroll.viewport = viewport.rectTransform;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 42;
            scroll.inertia = false;
            var track = CreatePanel(outer, "Scrollbar", Black);
            SetRect(track.rectTransform, new Vector2(1, 0), Vector2.one, new Vector2(-10, 0), Vector2.zero);
            var scrollbar = track.gameObject.AddComponent<Scrollbar>();
            var slidingArea = NewRect(track.transform, "Sliding Area");
            Stretch(slidingArea);
            var handle = CreatePanel(slidingArea, "Handle", Orange);
            Stretch(handle.rectTransform);
            scrollbar.handleRect = handle.rectTransform;
            scrollbar.targetGraphic = handle;
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scroll.verticalScrollbar = scrollbar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            return scroll;
        }

        public Slider CreateSlider(Transform parent, string name, float min, float max, float value, Action<float> changed)
        {
            var root = NewRect(parent, name);
            var slider = root.gameObject.AddComponent<Slider>();
            var background = CreatePanel(root, "Track", ButtonColor);
            SetRect(background.rectTransform, new Vector2(0, 0.35f), new Vector2(1, 0.65f), new Vector2(7, 0), new Vector2(-7, 0));
            var fillArea = NewRect(root, "Fill area");
            SetRect(fillArea, new Vector2(0, 0.35f), new Vector2(1, 0.65f), new Vector2(7, 0), new Vector2(-7, 0));
            var fill = CreatePanel(fillArea, "Fill", Orange);
            Stretch(fill.rectTransform);
            var handleArea = NewRect(root, "Handle area");
            SetRect(handleArea, new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(7, -13), new Vector2(-7, 13));
            var handle = CreatePanel(handleArea, "Handle", White);
            handle.rectTransform.sizeDelta = new Vector2(10, 0);
            slider.fillRect = fill.rectTransform;
            slider.handleRect = handle.rectTransform;
            slider.targetGraphic = handle;
            slider.minValue = min; slider.maxValue = max; slider.value = value;
            slider.onValueChanged.AddListener(position => changed(position));
            return slider;
        }

        public void AddBorder(RectTransform rect, Color color, float thickness = 2)
        {
            var left = CreatePanel(rect, "Border L", color); SetRect(left.rectTransform, Vector2.zero, new Vector2(0, 1), Vector2.zero, new Vector2(thickness, 0)); left.raycastTarget = false;
            var right = CreatePanel(rect, "Border R", color); SetRect(right.rectTransform, new Vector2(1, 0), Vector2.one, new Vector2(-thickness, 0), Vector2.zero); right.raycastTarget = false;
            var bottom = CreatePanel(rect, "Border B", color); SetRect(bottom.rectTransform, Vector2.zero, new Vector2(1, 0), Vector2.zero, new Vector2(0, thickness)); bottom.raycastTarget = false;
            var top = CreatePanel(rect, "Border T", color); SetRect(top.rectTransform, new Vector2(0, 1), Vector2.one, new Vector2(0, -thickness), Vector2.zero); top.raycastTarget = false;
        }

        public RectTransform CreateWindow(Transform parent, string name, string title, float headerHeight = 42)
        {
            var panel = CreatePanel(parent, name, Black);
            AddBorder(panel.rectTransform, Orange, 2);
            AddTitleBar(panel.rectTransform, title, headerHeight);
            return panel.rectTransform;
        }

        public TextMeshProUGUI AddTitleBar(RectTransform parent, string title, float height = 42)
        {
            var bar = CreatePanel(parent, title + " title bar", HeaderColor);
            SetRect(bar.rectTransform, new Vector2(0, 1), Vector2.one,
                new Vector2(2, -height - 2), new Vector2(-2, -2));
            bar.raycastTarget = false;
            var label = CreateText(bar.transform, title, 23, Orange, TextAlignmentOptions.Center);
            Stretch(label.rectTransform, 4);
            label.fontStyle = FontStyles.Bold;
            label.characterSpacing = 2;
            return label;
        }

        public static RectTransform NewRect(Transform parent, string name)
        {
            var result = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            result.gameObject.hideFlags = HideFlags.DontSave;
            result.gameObject.layer = 5;
            result.SetParent(parent, false);
            return result;
        }

        public static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        public static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = anchorMin; rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = offsetMin; rect.offsetMax = offsetMax;
        }

        public static void Stretch(RectTransform rect, float inset = 0) => SetRect(rect, Vector2.zero, Vector2.one, Vector2.one * inset, Vector2.one * -inset);
        public static void Clear(Transform parent)
        {
            for (var i = parent.childCount - 1; i >= 0; i--)
            {
                var child = parent.GetChild(i).gameObject;
                child.SetActive(false);
                Object.Destroy(child);
            }
        }

        private void FindFont()
        {
            var existing = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            var native = existing.FirstOrDefault(font => font != null && font.name.IndexOf("3270", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? Resources.FindObjectsOfTypeAll<TextMeshProUGUI>().Where(text => text != null && text.gameObject.scene.IsValid()).Select(text => text.font).FirstOrDefault(font => font != null)
                ?? TMP_Settings.defaultFontAsset;
            if (native != null)
            {
                _font = Object.Instantiate(native);
                _font.name = "LCReplay native font";
                _ownedAssets.Add(_font);
                _font.fallbackFontAssetTable = new List<TMP_FontAsset>(native.fallbackFontAssetTable ?? new List<TMP_FontAsset>());
            }
            try
            {
                CreateFileFont(native);
                if (_fallbackFont != null)
                {
                    if (_font == null) _font = _fallbackFont;
                    else _font.fallbackFontAssetTable.Add(_fallbackFont);
                    // Player names and recorded text can contain Unicode even though
                    // fixed captions are English. Add those glyphs when needed.
                }
            }
            catch (Exception error) { Debug.LogWarning("LCReplay Unicode fallback font: " + error.Message); }
        }

        private void CreateFileFont(TMP_FontAsset? native)
        {
            _fontFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "malgun.ttf");
            if (!File.Exists(_fontFile)) return;
            FontEngine.InitializeFontEngine();
            if (FontEngine.LoadFontFace(_fontFile, 48) != FontEngineError.Success)
                throw new InvalidOperationException("Cannot load the installed Malgun Gothic font file.");
            _addGlyph = typeof(FontEngine).GetMethod("TryAddGlyphToTexture", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (_addGlyph == null) throw new MissingMethodException("FontEngine.TryAddGlyphToTexture");
            var fallback = native != null ? Object.Instantiate(native) : ScriptableObject.CreateInstance<TMP_FontAsset>();
            _fallbackFont = fallback;
            _ownedAssets.Add(fallback);
            fallback.name = "LCReplay Unicode glyphs";
            SetFontProperty(fallback, "version", "1.1.0");
            SetFontProperty(fallback, "glyphTable", new List<Glyph>());
            SetFontProperty(fallback, "characterTable", new List<TMP_Character>());
            fallback.faceInfo = FontEngine.GetFaceInfo();
            fallback.atlasPopulationMode = AtlasPopulationMode.Static;
            fallback.isMultiAtlasTexturesEnabled = false;
            fallback.fallbackFontAssetTable = new List<TMP_FontAsset>();
            SetFontProperty(fallback, "atlasWidth", 2048);
            SetFontProperty(fallback, "atlasHeight", 2048);
            SetFontProperty(fallback, "atlasPadding", 4);
            SetFontProperty(fallback, "atlasRenderMode", GlyphRenderMode.SDFAA);
            _fontAtlas = new Texture2D(2048, 2048, TextureFormat.Alpha8, false) { name = "LCReplay Unicode atlas", hideFlags = HideFlags.DontSave };
            _fontAtlas.SetPixels32(new Color32[2048 * 2048]);
            _fontAtlas.Apply(false, false);
            _ownedAssets.Add(_fontAtlas);
            fallback.atlasTextures = new[] { _fontAtlas };
            var shader = Shader.Find("TextMeshPro/Mobile/Distance Field") ?? Shader.Find("TextMeshPro/Distance Field") ?? native?.material?.shader;
            if (shader == null) throw new InvalidOperationException("The game's TextMeshPro shader is unavailable.");
            var material = new Material(shader) { name = "LCReplay Unicode material", hideFlags = HideFlags.DontSave };
            _ownedAssets.Add(material);
            material.SetTexture("_MainTex", _fontAtlas);
            material.SetFloat("_TextureWidth", 2048); material.SetFloat("_TextureHeight", 2048);
            material.SetFloat("_GradientScale", 5);
            material.SetFloat("_WeightNormal", fallback.normalStyle); material.SetFloat("_WeightBold", fallback.boldStyle);
            fallback.material = material;
            _freeGlyphRects.Add(new GlyphRect(0, 0, 2047, 2047));
            fallback.ReadFontAssetDefinition();
        }

        private static void SetFontProperty(TMP_FontAsset font, string property, object value) =>
            typeof(TMP_FontAsset).GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(font, value, null);

        private bool EnsureGlyphs(string text)
        {
            if (_fallbackFont == null || _fontAtlas == null || _addGlyph == null || string.IsNullOrEmpty(text)) return false;
            var needed = text.Where(character => character >= 128 && !_fontCharacters.Contains(character) && (_font == null || !_font.HasCharacter(character))).Distinct().ToArray();
            if (needed.Length == 0 || FontEngine.LoadFontFace(_fontFile, 48) != FontEngineError.Success) return false;
            var changed = false;
            foreach (var character in needed)
            {
                _fontCharacters.Add(character);
                if (!FontEngine.TryGetGlyphIndex(character, out var index) || index == 0) continue;
                if (!_fontGlyphs.TryGetValue(index, out var glyph))
                {
                    var args = new object?[] { index, 4, GlyphPackingMode.BestShortSideFit, _freeGlyphRects, _usedGlyphRects, GlyphRenderMode.SDFAA, _fontAtlas, null };
                    if (!(_addGlyph.Invoke(null, args) is bool added) || !added || !(args[7] is Glyph packed)) continue;
                    glyph = packed;
                    glyph.atlasIndex = 0;
                    _fontGlyphs.Add(index, glyph);
                    _fallbackFont.glyphTable.Add(glyph);
                }
                _fallbackFont.characterTable.Add(new TMP_Character(character, _fallbackFont, glyph));
                changed = true;
            }
            if (changed) _fallbackFont.ReadFontAssetDefinition();
            return changed;
        }

        private void SuspendOtherInput()
        {
            foreach (var behaviour in Resources.FindObjectsOfTypeAll<Behaviour>())
            {
                if (behaviour == null || Owns(behaviour) || !behaviour.gameObject.scene.IsValid() || !behaviour.gameObject.scene.isLoaded) continue;
                if (!(behaviour is EventSystem) && !(behaviour is BaseInputModule) && !(behaviour is GraphicRaycaster)) continue;
                if (!_suspended.ContainsKey(behaviour)) _suspended.Add(behaviour, behaviour.enabled);
                behaviour.enabled = false;
            }
        }

        private void SuspendOtherSelectables()
        {
            // Navigation.Automatic searches all scene Selectables, even on a disabled
            // Canvas or GraphicRaycaster. Prevent keyboard submit from reaching them.
            foreach (var selectable in Resources.FindObjectsOfTypeAll<Selectable>())
            {
                if (selectable == null || Owns(selectable) || !selectable.gameObject.scene.IsValid() || !selectable.gameObject.scene.isLoaded) continue;
                if (!_selectableStates.ContainsKey(selectable)) _selectableStates.Add(selectable, selectable.interactable);
                selectable.interactable = false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (Root != null) { Root.SetActive(false); Object.Destroy(Root); }
                foreach (var asset in _ownedAssets) if (asset != null) Object.Destroy(asset);
                _ownedAssets.Clear();
                _textValues.Clear();
            }
            finally
            {
                foreach (var pair in _suspended) if (pair.Key != null) pair.Key.enabled = pair.Value;
                _suspended.Clear();
                foreach (var pair in _selectableStates) if (pair.Key != null) pair.Key.interactable = pair.Value;
                _selectableStates.Clear();
                if (_previousEventSystem != null)
                {
                    EventSystem.current = _previousEventSystem;
                    if (_previousSelection != null && _previousSelection.activeInHierarchy) _previousEventSystem.SetSelectedGameObject(_previousSelection);
                }
                if (_modal) { Cursor.lockState = _cursorLock; Cursor.visible = _cursorVisible; }
            }
        }
    }
}
