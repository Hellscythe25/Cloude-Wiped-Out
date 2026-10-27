using CloudGame.UI;
using UnityEngine;
using UnityEngine.UI;

namespace CloudGame.Online
{
    /// <summary>
    /// Pantalla de la partida online. Se construye entera por código y se dibuja encima del
    /// juego local (igual que <see cref="GameBoardView"/> construye su rejilla), así no hay que
    /// editar la escena.
    ///
    /// Es una vista tonta: no sabe jugar. Cada pulsación se convierte en una petición al
    /// servidor y lo único que se pinta es la <see cref="OnlineMatchView"/> que llega, ya sea
    /// como respuesta o como mensaje push del rival.
    /// </summary>
    public sealed class OnlineMatchScreen : MonoBehaviour
    {
        private const float PollSecondsWithPush = 10f;
        private const float PollSecondsWithoutPush = 3f;
        private const float ClaimTimeoutRetrySeconds = 3f;

        // Misma duración que MatchRules.GraceSeconds del servidor (+0,5 s de colchón).
        private const double GraceBeforeClaimingTimeout = 3.5;

        private static readonly Color Background = new Color(0.96f, 0.90f, 0.82f);
        private static readonly Color ButtonColor = new Color(0.55f, 0.37f, 0.29f);
        private static readonly Color TextColor = new Color(0.33f, 0.25f, 0.22f);

        private readonly OnlineMatchService service = new OnlineMatchService();

        private Font font;
        private GameObject overlay;
        private GameObject lobbyPanel;
        private GameObject matchPanel;

        private Text lobbyStatus;
        private InputField codeInput;
        private Button createButton;
        private Button joinButton;

        private Text roomText;
        private Text turnText;
        private Text livesText;
        private Text timerText;
        private Text infoText;
        private Text nextLabel;
        private Button nextButton;
        private Button forfeitButton;
        private GameBoardView boardView;
        private RectTransform boardRoot;
        private int builtSize = -1;

        private float nextPoll;
        private float nextClaim;

        /// <summary>Crea la pantalla y su botón "ONLINE" dentro del canvas indicado.</summary>
        public static OnlineMatchScreen Create(Canvas canvas, Font textFont)
        {
            var go = new GameObject("OnlineMatchScreen", typeof(RectTransform));
            go.transform.SetParent(canvas.transform, false);

            // Sin esto la raíz mide 100x100 en el centro y los anclajes de los hijos (arriba a la
            // derecha, etc.) se calcularían respecto a ese cuadradito y no respecto a la pantalla.
            Stretch((RectTransform)go.transform);

            var screen = go.AddComponent<OnlineMatchScreen>();
            screen.Build(textFont);
            return screen;
        }

        private void Awake()
        {
            service.ViewChanged += Render;
            service.Failed += ShowFailure;
            service.ResyncRequested += RequestResync;
        }

        private void OnDestroy()
        {
            service.ViewChanged -= Render;
            service.Failed -= ShowFailure;
            service.ResyncRequested -= RequestResync;
            _ = service.UnsubscribeAsync();
        }

        private void Update()
        {
            service.Pump();

            OnlineMatchView view = service.Current;
            if (overlay == null || !overlay.activeSelf || view == null)
            {
                return;
            }

            if (view.status == OnlineStatus.Playing)
            {
                double left = SecondsLeft(view);
                SetText(timerText, Mathf.Max(0, Mathf.CeilToInt((float)left)) + " s");

                // El servidor no tiene relojes: si el turno de alguien venció, hay que "despertarlo"
                // preguntando. Lo hace el primero de los dos que lo note.
                if (left < -GraceBeforeClaimingTimeout && Time.realtimeSinceStartup >= nextClaim && !service.IsBusy)
                {
                    nextClaim = Time.realtimeSinceStartup + ClaimTimeoutRetrySeconds;
                    _ = service.RefreshAsync();
                }
            }
            else
            {
                SetText(timerText, string.Empty);
            }

            // Llegó un aviso del rival: el aviso no trae estado, así que se pide el real.
            if (service.HasPendingRefresh && !service.IsBusy)
            {
                _ = service.RefreshAsync();
                return;
            }

            // Sondeo de respaldo: un push puede perderse, el estado leído no.
            if (!view.IsFinished && Time.realtimeSinceStartup >= nextPoll && !service.IsBusy)
            {
                nextPoll = Time.realtimeSinceStartup + (service.IsSubscribed ? PollSecondsWithPush : PollSecondsWithoutPush);
                _ = service.RefreshAsync();
            }
        }

        private double SecondsLeft(OnlineMatchView view)
        {
            return view.SecondsLeftAtServerTime() - (Time.realtimeSinceStartup - service.CurrentReceivedAt);
        }

        // ------------------------------------------------------------------ acciones

        private async void Open()
        {
            overlay.SetActive(true);
            overlay.transform.SetAsLastSibling();
            Render(service.Current);

            if (!service.IsReady)
            {
                ShowFailure("Inicia sesión antes de jugar online.");
                return;
            }

            await service.SubscribeAsync();

            if (service.Current != null)
            {
                _ = service.RefreshAsync();
            }
        }

        private void Close()
        {
            overlay.SetActive(false);
        }

        private void OnCreateClicked()
        {
            SetText(lobbyStatus, "Creando sala...");
            _ = service.CreateAsync();
        }

        private void OnJoinClicked()
        {
            string code = codeInput.text;
            if (string.IsNullOrWhiteSpace(code))
            {
                SetText(lobbyStatus, "Escribe el código de la sala.");
                return;
            }

            SetText(lobbyStatus, "Uniéndose...");
            _ = service.JoinAsync(code);
        }

        private void OnCellClicked(int index)
        {
            OnlineMatchView view = service.Current;
            if (view == null || !view.IsMyTurn)
            {
                SetText(infoText, view != null && view.IsFinished ? "La partida terminó." : "No es tu turno.");
                return;
            }

            _ = service.RevealAsync(index);
        }

        private void OnNextClicked()
        {
            _ = service.AdvanceLevelAsync();
        }

        private void OnForfeitClicked()
        {
            _ = service.ForfeitAsync();
        }

        private void OnLeaveClicked()
        {
            service.Leave();
            builtSize = -1;
            Render(null);
        }

        private void RequestResync()
        {
            if (service.Current != null && !service.IsBusy)
            {
                _ = service.RefreshAsync();
            }
        }

        // ------------------------------------------------------------------- vista

        private void Render(OnlineMatchView view)
        {
            if (overlay == null)
            {
                return;
            }

            lobbyPanel.SetActive(view == null);
            matchPanel.SetActive(view != null);

            if (view == null)
            {
                SetText(lobbyStatus, "Crea una sala o únete con el código de tu rival.");
                return;
            }

            nextPoll = Time.realtimeSinceStartup + PollSecondsWithPush;
            SetText(roomText, "ROOM " + view.matchCode);
            SetText(livesText, LivesLine(view));

            if (view.status == OnlineStatus.WaitingForGuest)
            {
                SetText(turnText, "WAITING FOR YOUR RIVAL");
                SetText(infoText, "Pásale este código: " + view.matchCode);
            }
            else if (view.IsFinished)
            {
                SetText(turnText, view.IWon ? "YOU WIN!" : "YOU LOSE");
                SetText(infoText, DescribeEnd(view));
            }
            else
            {
                SetText(turnText, "LEVEL " + view.level + " — " + (view.IsMyTurn ? "YOUR TURN" : "RIVAL'S TURN"));
                SetText(infoText, !string.IsNullOrEmpty(view.message) && view.outcome != OnlineOutcome.Applied
                    ? view.message
                    : DescribeEvent(view));
            }

            RenderBoard(view);

            bool canAdvance = view.IsMyTurn && (view.levelOwnerSeat == view.mySeat || view.isCleared);
            nextButton.interactable = canAdvance;
            SetText(nextLabel, view.isCleared ? "NEXT LEVEL" : "SKIP LEVEL (-1 LIFE)");
            forfeitButton.gameObject.SetActive(view.status == OnlineStatus.Playing);
        }

        private void RenderBoard(OnlineMatchView view)
        {
            boardRoot.gameObject.SetActive(view.size > 0 && view.cells != null && view.cells.Length > 0);
            if (view.size <= 0 || view.cells == null || view.cells.Length != view.size * view.size)
            {
                return;
            }

            var board = new PublicBoard(view);

            if (builtSize != view.size)
            {
                float spacing = view.size > 8 ? 4f : 8f;
                float cell = Mathf.Min(88f, (680f - spacing * (view.size - 1)) / view.size);
                boardView.SetLayout(cell, spacing);
                boardView.Build(board);
                builtSize = view.size;
            }

            boardView.Paint(board, view.IsFinished);
        }

        private static string LivesLine(OnlineMatchView view)
        {
            string me1 = view.mySeat == 1 ? " (YOU)" : string.Empty;
            string me2 = view.mySeat == 2 ? " (YOU)" : string.Empty;
            return "P1" + me1 + " x" + Mathf.Max(0, view.lives1) + "      P2" + me2 + " x" + Mathf.Max(0, view.lives2);
        }

        private static string DescribeEnd(OnlineMatchView view)
        {
            switch (view.endReason)
            {
                case "forfeit":
                    return view.IWon ? "Tu rival abandonó." : "Abandonaste la partida.";
                case "abandoned":
                    return view.IWon ? "Tu rival se desconectó." : "Se te consideró desconectado por inactividad.";
                default:
                    return "Sin vidas.";
            }
        }

        private static string DescribeEvent(OnlineMatchView view)
        {
            OnlineLastEvent e = view.lastEvent;
            if (e == null)
            {
                return string.Empty;
            }

            string who = e.seat == view.mySeat ? "Tú" : "Tu rival";
            switch (e.kind)
            {
                case "joined":
                    return "Tu rival se unió. ¡A jugar!";
                case "reveal":
                    return who + (e.wasMine ? " pisó una mina (-1 vida)." : " reveló una casilla.");
                case "advance":
                    return who + " pasó de nivel" + (e.livesLost > 0 ? " (-1 vida)." : ".");
                case "timeout":
                    return who + " se quedó sin tiempo (-1 vida).";
                default:
                    return string.Empty;
            }
        }

        private void ShowFailure(string message)
        {
            if (lobbyPanel != null && lobbyPanel.activeSelf)
            {
                SetText(lobbyStatus, message);
            }
            else
            {
                SetText(infoText, message);
            }
        }

        // ------------------------------------------------------- construcción de la UI

        private void Build(Font textFont)
        {
            font = textFont;

            // Botón de entrada, arriba a la derecha del juego local.
            Button entry = CreateButton(transform, "ONLINE", new Vector2(1f, 1f), new Vector2(-170f, -60f), new Vector2(260f, 80f), Open);
            entry.name = "OnlineEntryButton";

            overlay = CreatePanel(transform, "OnlineOverlay", Background);
            Stretch((RectTransform)overlay.transform);

            lobbyPanel = new GameObject("Lobby", typeof(RectTransform));
            lobbyPanel.transform.SetParent(overlay.transform, false);
            Stretch((RectTransform)lobbyPanel.transform);

            CreateLabel(lobbyPanel.transform, "ONLINE", 90, new Vector2(0.5f, 0.5f), new Vector2(0f, 300f), new Vector2(1000f, 130f));
            lobbyStatus = CreateLabel(lobbyPanel.transform, string.Empty, 38, new Vector2(0.5f, 0.5f), new Vector2(0f, 190f), new Vector2(1500f, 90f));
            createButton = CreateButton(lobbyPanel.transform, "CREATE ROOM", new Vector2(0.5f, 0.5f), new Vector2(0f, 60f), new Vector2(560f, 100f), OnCreateClicked);
            codeInput = CreateInput(lobbyPanel.transform, new Vector2(0.5f, 0.5f), new Vector2(-170f, -90f), new Vector2(300f, 100f));
            joinButton = CreateButton(lobbyPanel.transform, "JOIN", new Vector2(0.5f, 0.5f), new Vector2(170f, -90f), new Vector2(240f, 100f), OnJoinClicked);
            CreateButton(lobbyPanel.transform, "BACK", new Vector2(0.5f, 0.5f), new Vector2(0f, -260f), new Vector2(320f, 90f), Close);

            matchPanel = new GameObject("Match", typeof(RectTransform));
            matchPanel.transform.SetParent(overlay.transform, false);
            Stretch((RectTransform)matchPanel.transform);

            roomText = CreateLabel(matchPanel.transform, string.Empty, 40, new Vector2(0.5f, 1f), new Vector2(0f, -40f), new Vector2(900f, 60f));
            turnText = CreateLabel(matchPanel.transform, string.Empty, 56, new Vector2(0.5f, 1f), new Vector2(0f, -110f), new Vector2(1500f, 80f));
            livesText = CreateLabel(matchPanel.transform, string.Empty, 44, new Vector2(0.5f, 1f), new Vector2(0f, -190f), new Vector2(1500f, 70f));
            timerText = CreateLabel(matchPanel.transform, string.Empty, 56, new Vector2(1f, 1f), new Vector2(-170f, -130f), new Vector2(260f, 80f));
            infoText = CreateLabel(matchPanel.transform, string.Empty, 34, new Vector2(0.5f, 0f), new Vector2(0f, 170f), new Vector2(1700f, 70f));

            var boardGo = new GameObject("BoardRoot", typeof(RectTransform));
            boardGo.transform.SetParent(matchPanel.transform, false);
            boardRoot = (RectTransform)boardGo.transform;
            boardRoot.anchorMin = boardRoot.anchorMax = new Vector2(0.5f, 0.5f);
            boardRoot.sizeDelta = new Vector2(900f, 700f);
            boardRoot.anchoredPosition = new Vector2(0f, -10f);

            boardView = boardGo.AddComponent<GameBoardView>();
            boardView.Configure(boardRoot, font);
            boardView.Initialize(OnCellClicked);

            nextButton = CreateButton(matchPanel.transform, string.Empty, new Vector2(0.5f, 0f), new Vector2(0f, 90f), new Vector2(560f, 90f), OnNextClicked);
            nextLabel = nextButton.GetComponentInChildren<Text>();
            forfeitButton = CreateButton(matchPanel.transform, "FORFEIT", new Vector2(0f, 0f), new Vector2(190f, 90f), new Vector2(300f, 90f), OnForfeitClicked);
            CreateButton(matchPanel.transform, "LEAVE", new Vector2(1f, 0f), new Vector2(-190f, 90f), new Vector2(300f, 90f), OnLeaveClicked);

            overlay.SetActive(false);
        }

        private GameObject CreatePanel(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.AddComponent<Image>().color = color;
            return go;
        }

        private Text CreateLabel(Transform parent, string content, int size, Vector2 anchor, Vector2 position, Vector2 dimensions)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = anchor;
            rect.anchoredPosition = position;
            rect.sizeDelta = dimensions;

            Text text = go.AddComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = TextColor;
            text.text = content;
            text.raycastTarget = false;
            return text;
        }

        private Button CreateButton(Transform parent, string label, Vector2 anchor, Vector2 position, Vector2 dimensions, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject("Button_" + label, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = anchor;
            rect.anchoredPosition = position;
            rect.sizeDelta = dimensions;

            Image image = go.AddComponent<Image>();
            image.color = ButtonColor;
            Button button = go.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(onClick);

            Text text = CreateLabel(go.transform, label, 40, new Vector2(0.5f, 0.5f), Vector2.zero, dimensions);
            text.color = Color.white;
            Stretch((RectTransform)text.transform);
            return button;
        }

        private InputField CreateInput(Transform parent, Vector2 anchor, Vector2 position, Vector2 dimensions)
        {
            var go = new GameObject("CodeInput", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = anchor;
            rect.anchoredPosition = position;
            rect.sizeDelta = dimensions;

            Image image = go.AddComponent<Image>();
            image.color = Color.white;

            Text text = CreateLabel(go.transform, string.Empty, 56, new Vector2(0.5f, 0.5f), Vector2.zero, dimensions);
            Stretch((RectTransform)text.transform);
            text.raycastTarget = true;

            Text placeholder = CreateLabel(go.transform, "CODE", 48, new Vector2(0.5f, 0.5f), Vector2.zero, dimensions);
            placeholder.color = new Color(0.6f, 0.55f, 0.5f);
            Stretch((RectTransform)placeholder.transform);

            InputField input = go.AddComponent<InputField>();
            input.targetGraphic = image;
            input.textComponent = text;
            input.placeholder = placeholder;
            input.characterLimit = 4;
            input.contentType = InputField.ContentType.Alphanumeric;
            return input;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void SetText(Text target, string value)
        {
            if (target != null)
            {
                target.text = value;
            }
        }
    }
}
