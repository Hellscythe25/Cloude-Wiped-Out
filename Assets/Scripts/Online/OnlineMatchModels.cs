using System;
using System.Globalization;
using CloudGame.Gameplay;

namespace CloudGame.Online
{
    /// <summary>Estado de la sala (el mismo texto que envía el servidor).</summary>
    public static class OnlineStatus
    {
        public const string WaitingForGuest = "waiting_for_guest";
        public const string Playing = "playing";
        public const string Finished = "finished";
    }

    /// <summary>Veredicto del servidor a una petición. Un rechazo NO es un error: es parte del juego.</summary>
    public static class OnlineOutcome
    {
        public const string Applied = "applied";
        public const string Replayed = "replayed";
        public const string NotStarted = "not_started";
        public const string MatchFinished = "match_finished";
        public const string Stale = "stale";
        public const string NotYourTurn = "not_your_turn";
        public const string NotLevelOwner = "not_level_owner";
        public const string AlreadyRevealed = "already_revealed";
        public const string InvalidCell = "invalid_cell";
        public const string InvalidMove = "invalid_move";
        public const string Conflict = "conflict";
    }

    /// <summary>Qué fue lo último que pasó. Sirve para animar o escribir un mensaje.</summary>
    [Serializable]
    public class OnlineLastEvent
    {
        public string kind;
        public int seat;
        public int cell;
        public bool wasMine;
        public int livesLost;
    }

    /// <summary>
    /// Lo único que el servidor le cuenta a este cliente. Los nombres de los campos son los
    /// del JSON (camelCase) porque se lee con <c>JsonUtility</c>.
    ///
    /// Fíjate en lo que NO hay: ni la semilla ni las minas ocultas. <see cref="cells"/> trae
    /// -1 (oculta), -2 (mina ya pisada) o el número de minas vecinas (0-8); <see cref="mines"/>
    /// solo llega cuando la partida ya terminó.
    /// </summary>
    [Serializable]
    public class OnlineMatchView
    {
        public int protocolVersion;
        public string matchCode;
        public string status;
        public int mySeat;
        public int turnNumber;
        public int level;
        public int size;
        public int[] cells;
        public int lives1;
        public int lives2;
        public int currentSeat;
        public int levelOwnerSeat;
        public bool isCleared;
        public string turnDeadlineUtc;
        public string serverTimeUtc;
        public string outcome;
        public string message;
        public OnlineLastEvent lastEvent;
        public int winnerSeat;
        public string endReason;
        public int[] mines;

        public bool IsMyTurn => status == OnlineStatus.Playing && currentSeat == mySeat;

        public bool IsFinished => status == OnlineStatus.Finished;

        public bool IWon => IsFinished && winnerSeat == mySeat;

        public int LivesOfSeat(int seat) => seat == 1 ? lives1 : lives2;

        /// <summary>Segundos que quedan según el reloj del SERVIDOR (el del cliente solo sirve para ir contando).</summary>
        public double SecondsLeftAtServerTime()
        {
            if (!TryParse(turnDeadlineUtc, out DateTime deadline) || !TryParse(serverTimeUtc, out DateTime server))
            {
                return 0;
            }

            return (deadline - server).TotalSeconds;
        }

        private static bool TryParse(string text, out DateTime value)
        {
            // RoundtripKind respeta la "Z" del servidor (hora UTC). No se puede combinar con
            // AdjustToUniversal: esa mezcla lanza ArgumentException.
            bool ok = DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);
            if (ok)
            {
                value = value.ToUniversalTime();
            }

            return ok;
        }
    }

    /// <summary>
    /// El tablero tal y como lo cuenta el servidor. Implementa lo mismo que <see cref="Board"/>
    /// para poder dibujarse con el mismo <c>GameBoardView</c>, pero SOLO con información pública.
    /// </summary>
    public sealed class PublicBoard : IBoardReadModel
    {
        private readonly int[] cells;
        private readonly bool[] revealedMine;

        public PublicBoard(OnlineMatchView view)
        {
            cells = view.cells ?? new int[0];
            Size = view.size;

            // Las minas "sin pisar" solo llegan cuando la partida ya terminó.
            revealedMine = new bool[cells.Length];
            if (view.mines != null)
            {
                foreach (int index in view.mines)
                {
                    if (index >= 0 && index < revealedMine.Length)
                    {
                        revealedMine[index] = true;
                    }
                }
            }
        }

        public int Size { get; }

        public int CellCount => cells.Length;

        public bool IsRevealed(int index) => cells[index] != Board.HiddenCell;

        public bool IsMine(int index) => cells[index] == Board.MineCell || revealedMine[index];

        public int AdjacentMines(int index) => cells[index] > 0 ? cells[index] : 0;
    }
}
