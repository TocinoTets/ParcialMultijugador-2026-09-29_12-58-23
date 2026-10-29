using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace ProtocolExecution
{
    /// <summary>
    /// Interfaz completa del core loop del §2:
    ///   menu (host / unirse) -> lobby (personaje + dificultad + ready-check)
    ///   -> HUD de partida -> resumen final -> hub de mejoras -> nueva partida.
    ///
    /// La UI se construye por codigo a proposito: evita depender de un prefab de
    /// canvas cableado a mano y hace que la escena sea reproducible por script.
    /// </summary>
    public class GameUI : MonoBehaviour
    {
        private enum Screen
        {
            Menu = 0,
            Hub = 1,
            Lobby = 2,
            Hud = 3,
            Results = 4
        }

        private const int MaxPlayers = 3;

        // ---------------- Estado de la UI ----------------

        private Screen? _override;
        private string _addressInput = "127.0.0.1";
        private int _pendingCharacter;
        private int _pendingDifficulty = 1;
        private bool _pendingSeparateSpawns;
        private bool _pendingReady;
        private bool _lastReady;
        private bool _runRecorded;
        private string _statusMessage = string.Empty;

        // ---------------- Referencias de UI ----------------

        private Canvas _canvas;
        private GameObject _menuRoot;
        private GameObject _hubRoot;
        private GameObject _lobbyRoot;
        private GameObject _hudRoot;
        private GameObject _resultsRoot;

        private InputField _addressField;
        private Text _menuStatusText;

        private Text _lobbyPlayersText;
        private Text _lobbyInfoText;
        private Text _lobbyStartHintText;
        private readonly List<Button> _characterButtons = new List<Button>();
        private readonly List<Button> _difficultyButtons = new List<Button>();
        private Button _characterUnlockButton;
        private Button _readyButton;
        private Button _startButton;
        private Button _separateSpawnsButton;

        private Text _hudPlayerText;
        private Text _hudStatsText;
        private Text _hudTeamText;
        private Text _hudDownedText;
        private Image _hudHealthFill;
        private Text _hudHealthText;
        private Image _hudDashFill;
        private GameObject _bossBarRoot;
        private Image _bossHealthFill;
        private Text _bossHealthText;

        private Text _resultsTitleText;
        private Text _resultsBodyText;
        private Button _resultsContinueButton;
        private Button _resultsHubButton;

        private Text _hubPointsText;
        private Text _hubStatsText;
        private readonly List<Text> _upgradeLabels = new List<Text>();
        private readonly List<Button> _upgradeButtons = new List<Button>();

        private Font _font;
        private Sprite _fillSprite;

        /// <summary>Una Image sin sprite ignora fillAmount: las barras quedaban siempre llenas.</summary>
        private Sprite FillSprite => _fillSprite != null
            ? _fillSprite
            : _fillSprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f));

        private NetworkLauncher Launcher => NetworkLauncher.Instance;
        private RunController Run => RunController.Instance;

        // ---------------- Ciclo de vida ----------------

        private void Awake()
        {
            _font = ResolveFont();
            BuildCanvas();
            BuildMenu();
            BuildHub();
            BuildLobby();
            BuildHud();
            BuildResults();

            _pendingCharacter = 0;
            MetaProgression.Load();
        }

        private void Update()
        {
            Screen screen = ResolveScreen();
            ApplyScreen(screen);

            // Se rearma el registro de la run en cuanto la partida sale de Results.
            RunController run = Run;
            if (run == null || !run.IsSpawned || run.CurrentPhase != RunPhase.Results) _runRecorded = false;

            switch (screen)
            {
                case Screen.Menu: RefreshMenu(); break;
                case Screen.Hub: RefreshHub(); break;
                case Screen.Lobby: RefreshLobby(); break;
                case Screen.Hud: RefreshHud(); break;
                case Screen.Results: RefreshResults(); break;
            }
        }

        private Screen ResolveScreen()
        {
            if (_override.HasValue) return _override.Value;

            NetworkLauncher launcher = Launcher;
            if (launcher == null || !launcher.IsRunning) return Screen.Menu;

            RunController run = Run;
            if (run == null || !run.IsSpawned) return Screen.Lobby;

            switch (run.CurrentPhase)
            {
                case RunPhase.Lobby: return Screen.Lobby;
                case RunPhase.Playing: return Screen.Hud;
                default: return Screen.Results;
            }
        }

        private void ApplyScreen(Screen screen)
        {
            _menuRoot.SetActive(screen == Screen.Menu);
            _hubRoot.SetActive(screen == Screen.Hub);
            _lobbyRoot.SetActive(screen == Screen.Lobby);
            _hudRoot.SetActive(screen == Screen.Hud);
            _resultsRoot.SetActive(screen == Screen.Results);

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private static Font ResolveFont()
        {
            // En Unity 2022.2+ la fuente integrada se llama LegacyRuntime.ttf.
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font != null) return font;

            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (font != null) return font;

            return Font.CreateDynamicFontFromOSFont("Arial", 20);
        }

        // ---------------- Construccion de widgets ----------------

        private void BuildCanvas()
        {
            var canvasObject = new GameObject("GameCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);

            _canvas = canvasObject.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            // Se escala por altura: arriba, centro y abajo quedan siempre a la misma
            // distancia y los textos no se pisan en ventanas anchas.
            scaler.matchWidthOrHeight = 1f;

            EnsureEventSystem();
        }

        private void EnsureEventSystem()
        {
            if (FindFirstObjectByType<EventSystem>() != null) return;

            var eventSystem = new GameObject("EventSystem", typeof(EventSystem));

#if ENABLE_INPUT_SYSTEM
            // Con el Input System activo, el modulo legacy no recibe eventos de UI.
            eventSystem.AddComponent<InputSystemUIInputModule>();
#else
            eventSystem.AddComponent<StandaloneInputModule>();
#endif
        }

        private GameObject CreateRoot(string name)
        {
            var root = new GameObject(name, typeof(RectTransform));
            root.transform.SetParent(_canvas.transform, false);

            var rect = root.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            return root;
        }

        private static RectTransform CreateRect(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 anchoredPosition, Vector2 sizeDelta)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = sizeDelta;

            return rect;
        }

        private Image CreatePanel(Transform parent, string name, Color color, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 anchoredPosition, Vector2 sizeDelta)
        {
            RectTransform rect = CreateRect(parent, name, anchorMin, anchorMax, anchoredPosition, sizeDelta);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            return image;
        }

        private Text CreateLabel(Transform parent, string name, string content, int size, Color color,
            TextAnchor anchor, Vector2 anchorMin, Vector2 anchorMax, Vector2 anchoredPosition, Vector2 sizeDelta)
        {
            RectTransform rect = CreateRect(parent, name, anchorMin, anchorMax, anchoredPosition, sizeDelta);

            var text = rect.gameObject.AddComponent<Text>();
            text.font = _font;
            text.fontSize = size;
            text.text = content;
            text.color = color;
            text.alignment = anchor;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;

            return text;
        }

        private Button CreateButton(Transform parent, string name, string label, Vector2 anchor, Vector2 size,
            Vector2 anchoredPosition, Action onClick, Color? tint = null)
        {
            RectTransform rect = CreateRect(parent, name, anchor, anchor, anchoredPosition, size);

            var image = rect.gameObject.AddComponent<Image>();
            image.color = tint ?? new Color(0.09f, 0.13f, 0.20f, 0.96f);

            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;

            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.35f, 0.85f, 1f);
            colors.pressedColor = new Color(0.15f, 0.55f, 0.75f);
            colors.disabledColor = new Color(0.35f, 0.35f, 0.35f, 0.6f);
            colors.fadeDuration = 0.06f;
            button.colors = colors;

            Text text = CreateLabel(rect, "Label", label, 24, new Color(0.85f, 0.97f, 1f), TextAnchor.MiddleCenter,
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            text.rectTransform.offsetMin = Vector2.zero;
            text.rectTransform.offsetMax = Vector2.zero;

            if (onClick != null) button.onClick.AddListener(() => onClick());

            return button;
        }

        private static void SetButtonLabel(Button button, string label)
        {
            Text text = button.GetComponentInChildren<Text>();
            if (text != null) text.text = label;
        }

        private static void SetButtonEnabled(Button button, bool enabled)
        {
            if (button == null) return;
            button.interactable = enabled;
        }

        // ---------------- Pantalla: menu ----------------

        private void BuildMenu()
        {
            _menuRoot = CreateRoot("Screen_Menu");
            CreatePanel(_menuRoot.transform, "Background", new Color(0.02f, 0.03f, 0.06f, 1f),
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            CreateLabel(_menuRoot.transform, "Title", "PROTOCOL: EXECUTION",
                68, new Color(0.30f, 0.95f, 1f), TextAnchor.MiddleCenter,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -140f), new Vector2(1400f, 90f));

            CreateLabel(_menuRoot.transform, "Subtitle", "ROGUELITE COOPERATIVO  |  1-3 JUGADORES  |  CLIENTE / HOST",
                22, new Color(1f, 0.35f, 0.75f), TextAnchor.MiddleCenter,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -205f), new Vector2(1400f, 40f));

            CreateButton(_menuRoot.transform, "HostButton", "CREAR PARTIDA (HOST)", new Vector2(0.5f, 0.5f),
                new Vector2(460f, 70f), new Vector2(0f, 110f), OnHostClicked);

            CreateLabel(_menuRoot.transform, "AddressLabel", "DIRECCION DEL HOST (LAN)",
                18, new Color(0.6f, 0.75f, 0.9f), TextAnchor.MiddleCenter,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 30f), new Vector2(460f, 30f));

            CreateInputField(_menuRoot.transform, "AddressField", new Vector2(0.5f, 0.5f),
                new Vector2(460f, 60f), new Vector2(0f, -25f), _addressInput, value => _addressInput = value);

            CreateButton(_menuRoot.transform, "JoinButton", "UNIRSE A PARTIDA", new Vector2(0.5f, 0.5f),
                new Vector2(460f, 70f), new Vector2(0f, -115f), OnJoinClicked);

            CreateButton(_menuRoot.transform, "HubButton", "HUB DE MEJORAS", new Vector2(0.5f, 0.5f),
                new Vector2(460f, 70f), new Vector2(0f, -205f), () => _override = Screen.Hub);

            CreateButton(_menuRoot.transform, "QuitButton", "SALIR", new Vector2(0.5f, 0.5f),
                new Vector2(460f, 60f), new Vector2(0f, -295f), QuitGame);

            _menuStatusText = CreateLabel(_menuRoot.transform, "Status", string.Empty,
                20, new Color(1f, 0.7f, 0.3f), TextAnchor.MiddleCenter,
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 120f), new Vector2(1200f, 60f));

            CreateLabel(_menuRoot.transform, "Version", $"v{Application.version}  |  puerto {7777}  |  maximo {MaxPlayers} jugadores",
                16, new Color(0.45f, 0.55f, 0.65f), TextAnchor.MiddleCenter,
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 40f), new Vector2(1200f, 30f));
        }

        private void CreateInputField(Transform parent, string name, Vector2 anchor, Vector2 size,
            Vector2 anchoredPosition, string initial, Action<string> onChanged)
        {
            RectTransform rect = CreateRect(parent, name, anchor, anchor, anchoredPosition, size);

            var background = rect.gameObject.AddComponent<Image>();
            background.color = new Color(0.05f, 0.08f, 0.13f, 1f);

            var field = rect.gameObject.AddComponent<InputField>();

            Text textComponent = CreateLabel(rect, "Text", initial, 22, new Color(0.9f, 0.97f, 1f),
                TextAnchor.MiddleLeft, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(-20f, -10f));
            textComponent.supportRichText = false;

            Text placeholder = CreateLabel(rect, "Placeholder", "127.0.0.1", 22, new Color(0.4f, 0.5f, 0.6f),
                TextAnchor.MiddleLeft, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(-20f, -10f));
            placeholder.fontStyle = FontStyle.Italic;

            field.textComponent = textComponent;
            field.placeholder = placeholder;
            field.targetGraphic = background;
            field.text = initial;
            field.characterLimit = 46;

            if (onChanged != null) field.onValueChanged.AddListener(value => onChanged(value));

            _addressField = field;
        }

        private void RefreshMenu()
        {
            if (_menuStatusText == null) return;

            string message = NetworkLauncher.LastRejectReason;

            if (string.IsNullOrEmpty(message) && Launcher != null && !Launcher.IsRunning)
                message = "Sin conexion. Crea una partida o unite a la direccion del host.";

            _menuStatusText.text = message;
        }

        private void OnHostClicked()
        {
            NetworkLauncher launcher = Launcher;
            if (launcher == null) return;

            _override = null;
            if (!launcher.Host()) _statusMessage = "No se pudo iniciar el host.";
        }

        private void OnJoinClicked()
        {
            NetworkLauncher launcher = Launcher;
            if (launcher == null) return;

            if (_addressField != null) _addressInput = _addressField.text;

            _override = null;
            if (!launcher.Join(_addressInput)) _statusMessage = "No se pudo iniciar el cliente.";
        }

        private void QuitGame()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ---------------- Pantalla: hub de mejoras ----------------

        private void BuildHub()
        {
            _hubRoot = CreateRoot("Screen_Hub");
            CreatePanel(_hubRoot.transform, "Background", new Color(0.02f, 0.04f, 0.07f, 1f),
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            CreateLabel(_hubRoot.transform, "Title", "HUB  //  MEJORAS PERMANENTES",
                46, new Color(0.30f, 0.95f, 1f), TextAnchor.MiddleCenter,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -80f), new Vector2(1400f, 60f));

            _hubPointsText = CreateLabel(_hubRoot.transform, "Points", string.Empty,
                30, new Color(1f, 0.85f, 0.2f), TextAnchor.MiddleCenter,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -135f), new Vector2(1400f, 40f));

            _hubStatsText = CreateLabel(_hubRoot.transform, "Stats", string.Empty,
                20, new Color(0.6f, 0.75f, 0.9f), TextAnchor.MiddleCenter,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -175f), new Vector2(1400f, 30f));

            // Una fila por mejora, con su nivel, su coste y su boton de compra.
            for (int i = 0; i < GameData.Upgrades.Length; i++)
            {
                UpgradeDef upgrade = GameData.Upgrades[i];
                float y = 0f - i * 108f;

                CreatePanel(_hubRoot.transform, $"Row_{upgrade.id}", new Color(0.06f, 0.10f, 0.16f, 0.95f),
                    new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -280f + y), new Vector2(1100f, 92f));

                Text label = CreateLabel(_hubRoot.transform, $"Label_{upgrade.id}", string.Empty,
                    22, new Color(0.88f, 0.96f, 1f), TextAnchor.MiddleLeft,
                    new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-215f, -280f + y), new Vector2(620f, 84f));

                _upgradeLabels.Add(label);

                string id = upgrade.id;
                Button buy = CreateButton(_hubRoot.transform, $"Buy_{upgrade.id}", "COMPRAR",
                    new Vector2(0.5f, 1f), new Vector2(220f, 62f), new Vector2(415f, -280f + y),
                    () => OnBuyUpgradeClicked(id));
                _upgradeButtons.Add(buy);
            }

            // Desbloqueo de personajes.
            float unlockY = -300f - GameData.Upgrades.Length * 108f;

            _characterUnlockButton = CreateButton(_hubRoot.transform, "UnlockCharacter", "DESBLOQUEAR PERSONAJE",
                new Vector2(0.5f, 1f), new Vector2(420f, 64f), new Vector2(0f, unlockY),
                OnUnlockCharacterClicked);

            CreateButton(_hubRoot.transform, "BackButton", "VOLVER", new Vector2(0.5f, 0f),
                new Vector2(300f, 66f), new Vector2(0f, 90f), () => _override = null);

            CreateButton(_hubRoot.transform, "ResetButton", "BORRAR PROGRESO", new Vector2(0.5f, 0f),
                new Vector2(300f, 48f), new Vector2(0f, 30f), OnResetProfileClicked,
                new Color(0.25f, 0.06f, 0.09f, 0.95f));
        }

        private int _unlockCandidate;

        private void RefreshHub()
        {
            _hubPointsText.text = $"PUNTOS DISPONIBLES: {MetaProgression.Points}";
            _hubStatsText.text =
                $"Partidas jugadas: {MetaProgression.RunsPlayed}   |   Victorias: {MetaProgression.RunsWon}   |   " +
                $"Jefes derrotados: {MetaProgression.BossesKilled}   |   Puntos historicos: {MetaProgression.TotalPointsEarned}";

            for (int i = 0; i < GameData.Upgrades.Length; i++)
            {
                UpgradeDef upgrade = GameData.Upgrades[i];
                int level = MetaProgression.GetLevel(upgrade.id);
                int cost = MetaProgression.GetNextCost(upgrade.id);

                if (cost < 0)
                {
                    _upgradeLabels[i].text = $"{upgrade.displayName}  [{level}/{upgrade.maxLevel}]  MAXIMO\n{upgrade.description}";
                    SetButtonLabel(_upgradeButtons[i], "MAXIMO");
                    SetButtonEnabled(_upgradeButtons[i], false);
                }
                else
                {
                    _upgradeLabels[i].text = $"{upgrade.displayName}  [{level}/{upgrade.maxLevel}]  coste {cost}\n{upgrade.description}";
                    SetButtonLabel(_upgradeButtons[i], $"COMPRAR ({cost})");
                    SetButtonEnabled(_upgradeButtons[i], MetaProgression.CanBuy(upgrade.id));
                }
            }

            // El candidato empieza en el primer personaje bloqueado.
            if (_unlockCandidate <= 0 || MetaProgression.IsCharacterUnlocked(_unlockCandidate))
            {
                _unlockCandidate = -1;
                for (int i = 1; i < GameData.Characters.Length; i++)
                {
                    if (!MetaProgression.IsCharacterUnlocked(i))
                    {
                        _unlockCandidate = i;
                        break;
                    }
                }
            }

            if (_unlockCandidate < 0)
            {
                SetButtonLabel(_characterUnlockButton, "TODOS LOS PERSONAJES DESBLOQUEADOS");
                SetButtonEnabled(_characterUnlockButton, false);
            }
            else
            {
                int cost = MetaProgression.GetCharacterUnlockCost(_unlockCandidate);
                SetButtonLabel(_characterUnlockButton,
                    $"DESBLOQUEAR {GameData.GetCharacter(_unlockCandidate).displayName} ({cost})");
                SetButtonEnabled(_characterUnlockButton, MetaProgression.Points >= cost);
            }
        }

        private void OnBuyUpgradeClicked(string upgradeId)
        {
            MetaProgression.TryBuy(upgradeId);
        }

        private void OnUnlockCharacterClicked()
        {
            if (_unlockCandidate >= 0) MetaProgression.TryUnlockCharacter(_unlockCandidate);
        }

        private void OnResetProfileClicked()
        {
            MetaProgression.ResetProfile();
        }

        // ---------------- Pantalla: lobby ----------------

        private void BuildLobby()
        {
            _lobbyRoot = CreateRoot("Screen_Lobby");
            CreatePanel(_lobbyRoot.transform, "Background", new Color(0.02f, 0.03f, 0.06f, 1f),
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            CreateLabel(_lobbyRoot.transform, "Title", "SALA DE ESPERA",
                44, new Color(0.30f, 0.95f, 1f), TextAnchor.MiddleCenter,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -70f), new Vector2(1200f, 55f));

            CreateLabel(_lobbyRoot.transform, "CharHeader", "PERSONAJE",
                24, new Color(1f, 0.35f, 0.75f), TextAnchor.MiddleLeft,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-630f, -150f), new Vector2(400f, 30f));

            for (int i = 0; i < GameData.Characters.Length; i++)
            {
                int index = i;
                CharacterDef character = GameData.Characters[i];

                Button button = CreateButton(_lobbyRoot.transform, $"Char_{character.id}", character.displayName,
                    new Vector2(0.5f, 1f), new Vector2(400f, 62f), new Vector2(-430f, -210f - i * 74f),
                    () => OnCharacterClicked(index));
                _characterButtons.Add(button);
            }

            CreateLabel(_lobbyRoot.transform, "DiffHeader", "DIFICULTAD",
                24, new Color(1f, 0.35f, 0.75f), TextAnchor.MiddleLeft,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(30f, -150f), new Vector2(400f, 30f));

            for (int i = 0; i < GameData.Difficulties.Length; i++)
            {
                int index = i;
                DifficultyDef difficulty = GameData.Difficulties[i];

                Button button = CreateButton(_lobbyRoot.transform, $"Diff_{difficulty.id}", difficulty.displayName,
                    new Vector2(0.5f, 1f), new Vector2(400f, 62f), new Vector2(230f, -210f - i * 74f),
                    () => OnDifficultyClicked(index));
                _difficultyButtons.Add(button);
            }

            _separateSpawnsButton = CreateButton(_lobbyRoot.transform, "SeparateSpawns",
                "SPAWN: MISMA SALA", new Vector2(0.5f, 1f), new Vector2(400f, 62f),
                new Vector2(230f, -440f), OnSeparateSpawnsClicked);

            CreateLabel(_lobbyRoot.transform, "GroupHeader", "EQUIPO",
                24, new Color(1f, 0.35f, 0.75f), TextAnchor.MiddleLeft,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-630f, -450f), new Vector2(400f, 30f));

            // Debajo del titulo EQUIPO (antes arrancaba a la altura de los botones de personaje).
            _lobbyPlayersText = CreateLabel(_lobbyRoot.transform, "Players", string.Empty,
                20, new Color(0.88f, 0.96f, 1f), TextAnchor.UpperLeft,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-430f, -630f), new Vector2(400f, 300f));

            // Anclado arriba, debajo del boton SPAWN, para no depender del alto de la ventana.
            _lobbyInfoText = CreateLabel(_lobbyRoot.transform, "Info", string.Empty,
                19, new Color(0.7f, 0.85f, 1f), TextAnchor.UpperLeft,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(300f, -620f), new Vector2(560f, 220f));

            _readyButton = CreateButton(_lobbyRoot.transform, "ReadyButton", "LISTO",
                new Vector2(0.5f, 0f), new Vector2(340f, 70f), new Vector2(-190f, 130f), OnReadyClicked);

            _startButton = CreateButton(_lobbyRoot.transform, "StartButton", "INICIAR PARTIDA",
                new Vector2(0.5f, 0f), new Vector2(340f, 70f), new Vector2(190f, 130f), OnStartClicked,
                new Color(0.05f, 0.24f, 0.20f, 0.96f));

            _lobbyStartHintText = CreateLabel(_lobbyRoot.transform, "StartHint", string.Empty,
                18, new Color(1f, 0.75f, 0.35f), TextAnchor.MiddleCenter,
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 82f), new Vector2(1200f, 28f));

            CreateButton(_lobbyRoot.transform, "LeaveButton", "SALIR DE LA SESION",
                new Vector2(0.5f, 0f), new Vector2(340f, 46f), new Vector2(0f, 32f), OnLeaveClicked,
                new Color(0.25f, 0.06f, 0.09f, 0.95f));
        }

        private NetPlayer LocalPlayer => FindLocalPlayer();

        private static NetPlayer FindLocalPlayer()
        {
            for (int i = 0; i < NetPlayer.All.Count; i++)
            {
                NetPlayer player = NetPlayer.All[i];
                if (player != null && player.IsOwner) return player;
            }
            return null;
        }

        private void RefreshLobby()
        {
            NetworkLauncher launcher = Launcher;
            bool isHost = launcher != null && launcher.IsHost;

            NetPlayer local = LocalPlayer;

            // Mantiene sincronizada la UI con el estado que ya viajo por la red.
            if (local != null && local.IsSpawned)
            {
                _pendingCharacter = local.CharacterIndex.Value;
                _pendingReady = local.IsReady.Value;
                _lastReady = local.IsReady.Value;
            }

            RunController run = Run;
            if (run != null && run.IsSpawned)
            {
                _pendingDifficulty = run.DifficultyIndex.Value;
                _pendingSeparateSpawns = run.SeparateSpawns.Value;
            }

            // --- Personajes ---
            for (int i = 0; i < _characterButtons.Count; i++)
            {
                bool unlocked = MetaProgression.IsCharacterUnlocked(i);
                CharacterDef character = GameData.GetCharacter(i);
                bool selected = i == _pendingCharacter;

                SetButtonEnabled(_characterButtons[i], unlocked);

                string label = unlocked
                    ? $"{(selected ? "> " : "  ")}{character.displayName}  {Mathf.RoundToInt(MetaProgression.ResolveMaxHealth(i))} PV"
                    : $"  {character.displayName}  [BLOQUEADO]";

                SetButtonLabel(_characterButtons[i], label);
            }

            // --- Dificultad (solo el host la decide) ---
            for (int i = 0; i < _difficultyButtons.Count; i++)
            {
                DifficultyDef difficulty = GameData.GetDifficulty(i);
                bool selected = i == _pendingDifficulty;

                SetButtonEnabled(_difficultyButtons[i], isHost);
                SetButtonLabel(_difficultyButtons[i], $"{(selected ? "> " : "  ")}{difficulty.displayName}");
            }

            SetButtonEnabled(_separateSpawnsButton, isHost);
            SetButtonLabel(_separateSpawnsButton,
                _pendingSeparateSpawns ? "SPAWN: SALAS SEPARADAS" : "SPAWN: MISMA SALA");

            // --- Equipo ---
            var builder = new StringBuilder();
            int connected = 0;
            bool everyoneReady = true;

            NetworkManagerBridge.GetConnectedIds(out List<ulong> clients);
            connected = clients.Count;

            for (int i = 0; i < clients.Count; i++)
            {
                NetPlayer player = FindPlayer(clients[i]);
                if (player == null)
                {
                    builder.AppendLine($"  conectando...");
                    everyoneReady = false;
                    continue;
                }

                string mark = player.IsReady.Value ? "LISTO" : "esperando";
                bool self = player.IsOwner;
                builder.AppendLine($"{(self ? "> " : "  ")}{player.DisplayName}  ({player.Character.displayName})  -  {mark}");

                if (!player.IsReady.Value) everyoneReady = false;
            }

            builder.AppendLine();
            builder.AppendLine($"  {connected}/{MaxPlayers} jugadores");

            _lobbyPlayersText.text = builder.ToString();

            // --- Info del modo ---
            DifficultyDef diff = GameData.GetDifficulty(_pendingDifficulty);
            CharacterDef chr = GameData.GetCharacter(_pendingCharacter);

            _lobbyInfoText.text =
                $"DIFICULTAD: {diff.displayName}\n{diff.description}\n\n" +
                $"PERSONAJE: {chr.displayName}\n{chr.description}\n\n" +
                $"Vida {Mathf.RoundToInt(MetaProgression.ResolveMaxHealth(_pendingCharacter))}  |  " +
                $"Dano {MetaProgression.ResolveDamage(_pendingCharacter):0.#}  |  " +
                $"Velocidad {MetaProgression.ResolveSpeed(_pendingCharacter):0.#}\n" +
                $"Reduccion de dano: {MetaProgression.DamageReduction * 100f:0}%";

            // En solitario no hay a quien esperar: el host puede iniciar sin
            // pasar por el ready-check del equipo.
            bool soloHost = isHost && connected <= 1;

            SetButtonLabel(_readyButton, _pendingReady ? "CANCELAR LISTO" : "ESTOY LISTO");
            SetButtonEnabled(_startButton, isHost && (soloHost || everyoneReady) && connected > 0);

            if (!isHost)
                _lobbyStartHintText.text = "Solo el host puede iniciar la partida.";
            else if (soloHost)
                _lobbyStartHintText.text = "Partida en solitario: puedes iniciar cuando quieras.";
            else if (!everyoneReady)
                _lobbyStartHintText.text = "Esperando a que todo el equipo confirme que esta listo.";
            else
                _lobbyStartHintText.text = "Todo el equipo esta listo.";
        }

        private static NetPlayer FindPlayer(ulong clientId)
        {
            for (int i = 0; i < NetPlayer.All.Count; i++)
            {
                NetPlayer player = NetPlayer.All[i];
                if (player != null && player.OwnerClientId == clientId) return player;
            }
            return null;
        }

        private void OnCharacterClicked(int index)
        {
            if (!MetaProgression.IsCharacterUnlocked(index)) return;

            NetPlayer local = LocalPlayer;
            if (local != null && local.IsSpawned) local.CharacterIndex.Value = index;

            _pendingCharacter = index;
        }

        private void OnDifficultyClicked(int index)
        {
            RunController run = Run;
            if (run == null || !run.IsSpawned || !run.IsServer) return;

            // Se escribe en la variable replicada: RefreshLobby relee de ahi cada frame.
            _pendingDifficulty = index;
            run.DifficultyIndex.Value = index;
        }

        private void OnSeparateSpawnsClicked()
        {
            RunController run = Run;
            if (run == null || !run.IsSpawned || !run.IsServer) return;

            _pendingSeparateSpawns = !_pendingSeparateSpawns;
            run.SeparateSpawns.Value = _pendingSeparateSpawns;
        }

        private void OnReadyClicked()
        {
            NetPlayer local = LocalPlayer;
            if (local == null || !local.IsSpawned) return;

            _pendingReady = !_pendingReady;
            _lastReady = _pendingReady;
            local.IsReady.Value = _pendingReady;
        }

        private void OnStartClicked()
        {
            RunController run = Run;
            if (run == null || !run.IsSpawned || !run.IsServer) return;

            IReadOnlyList<ulong> clients = NetworkManagerBridge.ConnectedIds();

            // El ready-check solo tiene sentido cuando hay mas de un jugador.
            if (clients.Count > 1)
            {
                foreach (ulong clientId in clients)
                {
                    NetPlayer player = FindPlayer(clientId);
                    if (player == null || !player.IsReady.Value) return;
                }
            }

            // §4.4: la semilla la decide el host. Es lo que garantiza que todos
            // construyan el mismo piso.
            int seed = UnityEngine.Random.Range(1, int.MaxValue);
            run.ServerBeginRun(_pendingDifficulty, _pendingSeparateSpawns, seed);
        }

        private void OnLeaveClicked()
        {
            NetworkLauncher launcher = Launcher;
            if (launcher != null) launcher.Shutdown();

            _override = null;
        }

        // ---------------- Pantalla: HUD ----------------

        private void BuildHud()
        {
            _hudRoot = CreateRoot("Screen_Hud");

            // Barra de vida propia, abajo a la izquierda.
            CreatePanel(_hudRoot.transform, "HealthBack", new Color(0.05f, 0.06f, 0.09f, 0.85f),
                new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(220f, 90f), new Vector2(400f, 34f));

            RectTransform fillRect = CreateRect(_hudRoot.transform, "HealthFill",
                new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(220f, 90f), new Vector2(394f, 28f));
            _hudHealthFill = fillRect.gameObject.AddComponent<Image>();
            _hudHealthFill.sprite = FillSprite;
            _hudHealthFill.type = Image.Type.Filled;
            _hudHealthFill.fillMethod = Image.FillMethod.Horizontal;
            _hudHealthFill.fillAmount = 1f;
            _hudHealthFill.color = new Color(0.2f, 0.95f, 0.55f);

            _hudHealthText = CreateLabel(_hudRoot.transform, "HealthText", "100 / 100",
                20, Color.white, TextAnchor.MiddleCenter,
                new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(220f, 130f), new Vector2(400f, 28f));

            // Barra de dash, debajo de la vida.
            CreatePanel(_hudRoot.transform, "DashBack", new Color(0.05f, 0.06f, 0.09f, 0.85f),
                new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(220f, 55f), new Vector2(400f, 18f));

            RectTransform dashRect = CreateRect(_hudRoot.transform, "DashFill",
                new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(220f, 55f), new Vector2(396f, 14f));
            _hudDashFill = dashRect.gameObject.AddComponent<Image>();
            _hudDashFill.sprite = FillSprite;
            _hudDashFill.type = Image.Type.Filled;
            _hudDashFill.fillMethod = Image.FillMethod.Horizontal;
            _hudDashFill.fillAmount = 1f;
            _hudDashFill.color = new Color(0.35f, 0.8f, 1f);

            _hudPlayerText = CreateLabel(_hudRoot.transform, "PlayerInfo", string.Empty,
                20, new Color(0.88f, 0.96f, 1f), TextAnchor.LowerLeft,
                new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(240f, 165f), new Vector2(460f, 30f));

            _hudStatsText = CreateLabel(_hudRoot.transform, "TeamInfo", string.Empty,
                20, new Color(1f, 0.85f, 0.2f), TextAnchor.UpperRight,
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-250f, -85f), new Vector2(460f, 130f));

            _hudTeamText = CreateLabel(_hudRoot.transform, "TeamList", string.Empty,
                18, new Color(0.75f, 0.9f, 1f), TextAnchor.UpperLeft,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(230f, -105f), new Vector2(420f, 170f));

            _hudDownedText = CreateLabel(_hudRoot.transform, "Downed", string.Empty,
                46, new Color(1f, 0.25f, 0.35f), TextAnchor.MiddleCenter,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f), new Vector2(900f, 70f));

            // Barra del jefe, arriba al centro.
            _bossBarRoot = CreatePanel(_hudRoot.transform, "BossBar", new Color(0.05f, 0.06f, 0.09f, 0.9f),
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -50f), new Vector2(900f, 30f)).gameObject;

            RectTransform bossFill = CreateRect(_bossBarRoot.transform, "BossFill",
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(892f, 24f));
            _bossHealthFill = bossFill.gameObject.AddComponent<Image>();
            _bossHealthFill.sprite = FillSprite;
            _bossHealthFill.type = Image.Type.Filled;
            _bossHealthFill.fillMethod = Image.FillMethod.Horizontal;
            _bossHealthFill.fillAmount = 1f;
            _bossHealthFill.color = new Color(0.95f, 0.25f, 0.45f);

            _bossHealthText = CreateLabel(_hudRoot.transform, "BossName", string.Empty,
                20, new Color(1f, 0.6f, 0.7f), TextAnchor.MiddleCenter,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -85f), new Vector2(900f, 28f));

            _bossBarRoot.SetActive(false);

            CreateButton(_hudRoot.transform, "LeaveRunButton", "ABANDONAR PARTIDA",
                new Vector2(1f, 0f), new Vector2(280f, 46f), new Vector2(-170f, 40f), OnLeaveClicked,
                new Color(0.25f, 0.06f, 0.09f, 0.95f));
        }

        private void RefreshHud()
        {
            NetPlayer local = LocalPlayer;
            RunController run = Run;

            if (local != null && local.IsSpawned)
            {
                float max = Mathf.Max(1f, local.MaxHealth.Value);
                float current = Mathf.Clamp(local.Health.Value, 0f, max);

                _hudHealthFill.fillAmount = current / max;
                _hudHealthFill.color = Color.Lerp(new Color(1f, 0.25f, 0.3f), new Color(0.2f, 0.95f, 0.55f), current / max);
                _hudHealthText.text = $"{Mathf.CeilToInt(current)} / {Mathf.CeilToInt(max)}";

                // El dash se recarga por tiempo local; la barra es solo informativa.
                _hudDashFill.fillAmount = 1f;

                _hudPlayerText.text = $"{local.DisplayName}  //  {local.Character.displayName}  //  bajas {local.Kills.Value}";
                _hudDownedText.text = local.IsDowned.Value ? "CAIDO - ESPERANDO REANIMACION" : string.Empty;
            }
            else
            {
                _hudHealthText.text = string.Empty;
                _hudDownedText.text = string.Empty;
            }

            if (run != null && run.IsSpawned)
            {
                _hudStatsText.text =
                    $"PUNTOS DEL EQUIPO: {run.TeamPoints.Value}\n" +
                    $"PISO {run.Floor.Value}  //  {run.Difficulty.displayName}\n" +
                    $"JUGADORES: {run.ConnectedPlayerCount()}";

                bool bossVisible = run.BossSpawned.Value && NetBoss.Current != null;
                _bossBarRoot.SetActive(bossVisible);

                if (bossVisible)
                {
                    int max = Mathf.Max(1, run.BossMaxHealth.Value);
                    int current = Mathf.Clamp(run.BossHealth.Value, 0, max);
                    _bossHealthFill.fillAmount = current / (float)max;
                    _bossHealthText.text = $"NUCLEO CENTRAL  //  {current} / {max}";
                }
            }

            var builder = new StringBuilder();
            for (int i = 0; i < NetPlayer.All.Count; i++)
            {
                NetPlayer player = NetPlayer.All[i];
                if (player == null) continue;

                string state = player.IsDowned.Value ? "CAIDO" : $"{Mathf.CeilToInt(player.Health.Value)} PV";
                builder.AppendLine($"{(player.IsOwner ? "> " : "  ")}{player.DisplayName}  {state}  bajas {player.Kills.Value}");
            }
            _hudTeamText.text = builder.ToString();
        }

        // ---------------- Pantalla: resultados ----------------

        private void BuildResults()
        {
            _resultsRoot = CreateRoot("Screen_Results");
            CreatePanel(_resultsRoot.transform, "Background", new Color(0.02f, 0.03f, 0.06f, 0.97f),
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            _resultsTitleText = CreateLabel(_resultsRoot.transform, "Title", string.Empty,
                70, new Color(0.30f, 0.95f, 1f), TextAnchor.MiddleCenter,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -180f), new Vector2(1400f, 90f));

            _resultsBodyText = CreateLabel(_resultsRoot.transform, "Body", string.Empty,
                26, new Color(0.88f, 0.96f, 1f), TextAnchor.UpperCenter,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 30f), new Vector2(1200f, 360f));

            _resultsContinueButton = CreateButton(_resultsRoot.transform, "Continue", "VOLVER A LA SALA DE ESPERA",
                new Vector2(0.5f, 0f), new Vector2(420f, 70f), new Vector2(-230f, 130f), OnResultsContinueClicked);

            _resultsHubButton = CreateButton(_resultsRoot.transform, "ToHub", "IR AL HUB DE MEJORAS",
                new Vector2(0.5f, 0f), new Vector2(420f, 70f), new Vector2(230f, 130f),
                () => _override = Screen.Hub);
        }

        private void RefreshResults()
        {
            RunController run = Run;
            if (run == null || !run.IsSpawned) return;

            bool won = run.RunWon.Value;

            // El progreso se persiste una unica vez por jugador al ver el resumen.
            // No se espera a un clic porque el boton de continuar es solo del host.
            if (!_runRecorded)
            {
                _runRecorded = true;
                MetaProgression.RecordRun(won, run.TeamPoints.Value, won);
            }

            _resultsTitleText.text = won ? "PROTOCOLO COMPLETADO" : "EJECUCION FALLIDA";
            _resultsTitleText.color = won ? new Color(0.3f, 1f, 0.6f) : new Color(1f, 0.3f, 0.4f);

            var builder = new StringBuilder();
            builder.AppendLine($"Puntos recolectados en la run: {run.TeamPoints.Value}");
            builder.AppendLine($"Dificultad: {run.Difficulty.displayName}  (multiplicador x{run.Difficulty.pointsMul:0.00})");
            builder.AppendLine($"Jugadores: {run.ConnectedPlayerCount()}   |   Jefe derrotado: {(run.BossSpawned.Value && !won ? "no" : (won ? "si" : "no"))}");
            builder.AppendLine();

            for (int i = 0; i < NetPlayer.All.Count; i++)
            {
                NetPlayer player = NetPlayer.All[i];
                if (player == null) continue;
                builder.AppendLine($"{player.DisplayName} ({player.Character.displayName})  bajas {player.Kills.Value}  caidas {player.Deaths.Value}");
            }

            builder.AppendLine();
            builder.AppendLine("Los puntos se suman a tu perfil al volver al hub.");
            _resultsBodyText.text = builder.ToString();

            // El host devuelve a todo el grupo al lobby; el cliente espera.
            SetButtonEnabled(_resultsContinueButton, run.IsServer);
            SetButtonLabel(_resultsContinueButton,
                run.IsServer ? "VOLVER A LA SALA DE ESPERA" : "ESPERANDO AL HOST...");
        }

        private void OnResultsContinueClicked()
        {
            RunController run = Run;
            if (run != null && run.IsSpawned && run.IsServer) run.ServerReturnToLobby();
            _override = null;
        }
    }

    /// <summary>
    /// Acceso a la lista de clientes conectados sin exponer NetworkManager por
    /// todo el codigo de UI.
    /// </summary>
    internal static class NetworkManagerBridge
    {
        public static void GetConnectedIds(out List<ulong> clients)
        {
            clients = new List<ulong>();
            var manager = Unity.Netcode.NetworkManager.Singleton;
            if (manager == null) return;

            foreach (ulong id in manager.ConnectedClientsIds) clients.Add(id);
        }

        public static IReadOnlyList<ulong> ConnectedIds()
        {
            GetConnectedIds(out List<ulong> clients);
            return clients;
        }

        public static string PlayerName(ulong clientId)
        {
            return $"JUGADOR-{clientId + 1}";
        }
    }
}
