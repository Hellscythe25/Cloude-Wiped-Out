using CloudGame.Gameplay;
using CloudGame.Online;
using CloudGame.Save;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace CloudGame.UI
{
    /// <summary>
    /// Pantalla de partida: conecta <see cref="MatchEngine"/> con el HUD y el
    /// tablero. El motor decide las reglas; aquí solo se refleja el estado y se
    /// capturan las pulsaciones.
    /// </summary>
    public sealed class GameScreenController : MonoBehaviour
    {
        [Header("Tablero")]
        [SerializeField] private GameBoardView boardView;

        [Header("HUD")]
        [SerializeField] private Text levelText;
        [SerializeField] private Text turnText;
        [SerializeField] private Text playerOneLivesText;
        [SerializeField] private Text playerTwoLivesText;

        [Header("Acciones")]
        [SerializeField] private Button nextLevelButton;
        [SerializeField] private Text nextLevelLabel;
        [SerializeField] private Button menuButton;

        [Header("Fin de partida")]
        [SerializeField] private GameObject gameOverPanel;
        [SerializeField] private Text gameOverTitle;
        [SerializeField] private Text gameOverStats;
        [SerializeField] private Button playAgainButton;
        [SerializeField] private Button gameOverMenuButton;

        [Header("Persistencia")]
        [SerializeField] private CloudSaveManager cloudSave;

        private MatchEngine match;

        /// <summary>
        /// El gestor de guardado persiste entre escenas, así que si la escena no
        /// lo referencia directamente se usa el singleton.
        /// </summary>
        private CloudSaveManager CloudSave => cloudSave != null ? cloudSave : CloudSaveManager.Instance;

        private void Awake()
        {
            if (boardView != null)
            {
                boardView.Initialize(this);
            }

            if (menuButton != null)
            {
                menuButton.onClick.AddListener(GoToMenu);
            }

            if (gameOverMenuButton != null)
            {
                gameOverMenuButton.onClick.AddListener(GoToMenu);
            }

            if (nextLevelButton != null)
            {
                nextLevelButton.onClick.AddListener(OnNextLevelClicked);
            }

            if (playAgainButton != null)
            {
                playAgainButton.onClick.AddListener(StartMatch);
            }

            AttachOnlineScreen();
            StartMatch();
        }

        /// <summary>
        /// Añade el botón ONLINE y la pantalla de partida en línea encima del juego local.
        /// Se construye por código para no depender de editar la escena.
        /// </summary>
        private void AttachOnlineScreen()
        {
            Canvas canvas = menuButton != null ? menuButton.GetComponentInParent<Canvas>() : FindFirstObjectByType<Canvas>();
            if (canvas != null && levelText != null)
            {
                OnlineMatchScreen.Create(canvas.rootCanvas, levelText.font);
            }
        }

        public void StartMatch()
        {
            match = new MatchEngine();

            SetGameOverVisible(false);

            if (nextLevelButton != null)
            {
                nextLevelButton.interactable = true;
            }

            if (boardView != null)
            {
                boardView.Build(match.Board);
                boardView.Paint(match.Board, false);
            }

            RefreshHud();
        }

        public void OnCellClicked(int index)
        {
            if (match == null || match.Phase != MatchPhase.InProgress)
            {
                return;
            }

            if (index < 0 || index >= match.Board.CellCount || match.Board.IsRevealed(index))
            {
                return;
            }

            RevealResult result = match.Reveal(index);

            if (result.Outcome == RevealOutcome.Mine)
            {
                CloudSave?.RecordMinesRevealed();
            }

            if (boardView != null)
            {
                boardView.Paint(match.Board, result.MatchEnded);
            }

            RefreshHud();

            if (result.MatchEnded)
            {
                ShowGameOver();
            }
        }

        private void OnNextLevelClicked()
        {
            if (match == null || match.Phase != MatchPhase.InProgress)
            {
                return;
            }

            int completedLevel = match.Level;
            AdvanceResult result = match.AdvanceLevel();

            if (result.Outcome == AdvanceOutcome.NotLevelOwner)
            {
                SetTurnText("ONLY PLAYER " + Number(match.LevelOwner) + " CAN MOVE ON");
                return;
            }

            if (result.Outcome == AdvanceOutcome.Advanced)
            {
                // match.Level ya es el nivel nuevo, asi que se guarda el que se acaba de superar.
                CloudSave?.RecordLevelCompleted(completedLevel);

                // Antes el progreso solo se anotaba en memoria y nunca se subía a Cloud Save.
                _ = CloudSave?.SaveAsync();
            }

            if (boardView != null)
            {
                boardView.Build(match.Board);
                boardView.Paint(match.Board, false);
            }

            RefreshHud();

            if (result.MatchEnded)
            {
                ShowGameOver();
            }
        }

        private void RefreshHud()
        {
            if (match == null)
            {
                return;
            }

            SetText(levelText, "LEVEL " + match.Level);
            SetTurnText(match.Phase == MatchPhase.Finished
                ? "GAME OVER"
                : "PLAYER " + Number(match.CurrentPlayer) + "'S TURN");
            SetText(playerOneLivesText, Lives(match.PlayerOneLives));
            SetText(playerTwoLivesText, Lives(match.PlayerTwoLives));

            if (nextLevelLabel != null)
            {
                nextLevelLabel.text = match.Board.IsCleared
                    ? "NEXT LEVEL"
                    : "SKIP LEVEL (-1 LIFE)";
            }
        }

        private void SetTurnText(string value)
        {
            SetText(turnText, value);
        }

        private void ShowGameOver()
        {
            Player winner = match.Winner;
            SetText(gameOverTitle, "PLAYER " + Number(winner) + " WINS!");
            SetText(
                gameOverStats,
                "Reached level " + match.Level +
                "\nPlayer 1: " + match.PlayerOneLives + " lives left" +
                "\nPlayer 2: " + match.PlayerTwoLives + " lives left");

            SetGameOverVisible(true);

            if (nextLevelButton != null)
            {
                nextLevelButton.interactable = false;
            }

            CloudSave?.RecordGameLost();
            _ = CloudSave?.SaveAsync();
        }

        private void SetGameOverVisible(bool visible)
        {
            if (gameOverPanel != null)
            {
                gameOverPanel.SetActive(visible);
            }
        }

        private void GoToMenu()
        {
            SceneManager.LoadScene("SampleScene");
        }

        private static string Lives(int value)
        {
            return "x" + Mathf.Max(0, value);
        }

        private static int Number(Player player)
        {
            return player == Player.PlayerOne ? 1 : 2;
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
