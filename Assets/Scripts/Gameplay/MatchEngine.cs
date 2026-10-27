using System;
using System.Collections.Generic;

namespace CloudGame.Gameplay
{
    public enum Player
    {
        PlayerOne,
        PlayerTwo,
    }

    public enum MatchPhase
    {
        InProgress,
        Finished,
    }

    public enum RevealOutcome
    {
        Safe,
        Mine,
        AlreadyRevealed,
        MatchFinished,
        InvalidCell,
        TimedOut,
    }

    public enum AdvanceOutcome
    {
        Advanced,
        NotLevelOwner,
        MatchFinished,
    }

    /// <summary>Resultado de revelar una casilla, con el efecto sobre vidas y turno.</summary>
    public readonly struct RevealResult
    {
        public RevealResult(
            RevealOutcome outcome,
            Player whoPlayed,
            Player nextPlayer,
            int livesLost,
            int livesRemaining,
            bool matchEnded,
            IReadOnlyList<int> revealedCells)
        {
            Outcome = outcome;
            WhoPlayed = whoPlayed;
            NextPlayer = nextPlayer;
            LivesLost = livesLost;
            LivesRemaining = livesRemaining;
            MatchEnded = matchEnded;
            RevealedCells = revealedCells;
        }

        public RevealOutcome Outcome { get; }

        public Player WhoPlayed { get; }

        public Player NextPlayer { get; }

        public int LivesLost { get; }

        public int LivesRemaining { get; }

        public bool MatchEnded { get; }

        public IReadOnlyList<int> RevealedCells { get; }
    }

    /// <summary>Resultado de intentar pasar al siguiente nivel.</summary>
    public readonly struct AdvanceResult
    {
        public AdvanceResult(
            AdvanceOutcome outcome,
            int level,
            int livesLost,
            int livesRemaining,
            bool skippedCellsPenalty,
            bool matchEnded)
        {
            Outcome = outcome;
            Level = level;
            LivesLost = livesLost;
            LivesRemaining = livesRemaining;
            SkippedCellsPenalty = skippedCellsPenalty;
            MatchEnded = matchEnded;
        }

        public AdvanceOutcome Outcome { get; }

        public int Level { get; }

        public int LivesLost { get; }

        public int LivesRemaining { get; }

        public bool SkippedCellsPenalty { get; }

        public bool MatchEnded { get; }
    }

    /// <summary>
    /// Reglas de la partida: 3 vidas por jugador, el turno pasa siempre tras
    /// revelar (sea casilla segura o mina), los niveles impares son del
    /// Jugador 1 y los pares del Jugador 2, y solo el responsable del nivel
    /// puede avanzar. Quedarse con casillas ocultas al avanzar cuesta una vida.
    ///
    /// El motor no sabe nada de redes ni de Unity. Para jugar online lo ejecuta
    /// el servidor: por eso cada nivel se genera de forma <b>determinista</b> a
    /// partir de la semilla de la partida y del número de nivel. Con la misma
    /// semilla y la misma lista de jugadas se llega siempre al mismo estado, así que
    /// el servidor no guarda una foto: guarda la lista de jugadas y la reproduce.
    /// </summary>
    public sealed class MatchEngine
    {
        public const int MaxLives = 3;

        private readonly int matchSeed;
        private Board board;
        private int playerOneLives;
        private int playerTwoLives;

        public MatchEngine(int seed = 0)
        {
            matchSeed = seed == 0 ? new Random().Next(1, int.MaxValue) : seed;
            Level = 1;
            playerOneLives = MaxLives;
            playerTwoLives = MaxLives;
            CurrentPlayer = LevelOwnerFor(1);
            board = BuildBoard(Level);
        }

        public int Level { get; private set; }

        public Player CurrentPlayer { get; private set; }

        public MatchPhase Phase { get; private set; } = MatchPhase.InProgress;

        /// <summary>
        /// Cuenta las acciones que han cambiado la partida (revelar, avanzar, agotar
        /// el turno, abandonar). Una jugada que se rechaza no lo incrementa. Permite
        /// a un cliente decir "quiero jugar sobre el turno N" y al servidor rechazarlo
        /// si el estado ya avanzó.
        /// </summary>
        public int TurnNumber { get; private set; }

        public Board Board => board;

        public int PlayerOneLives => playerOneLives;

        public int PlayerTwoLives => playerTwoLives;

        public Player LevelOwner => LevelOwnerFor(Level);

        public bool IsCurrentPlayerLevelOwner => CurrentPlayer == LevelOwner;

        public int LivesOf(Player player) => player == Player.PlayerOne ? playerOneLives : playerTwoLives;

        public Player Winner
        {
            get
            {
                if (Phase != MatchPhase.Finished)
                {
                    return CurrentPlayer;
                }

                if (playerOneLives > 0)
                {
                    return Player.PlayerOne;
                }

                return Player.PlayerTwo;
            }
        }

        /// <summary>Niveles impares para el Jugador 1, pares para el Jugador 2.</summary>
        public static Player LevelOwnerFor(int level)
        {
            return level % 2 == 1 ? Player.PlayerOne : Player.PlayerTwo;
        }

        /// <summary>
        /// Revela una casilla. Siempre pasa el turno, tanto si es segura como si
        /// es mina, tal y como marca el diseño. Poner una mina resta una vida.
        /// Un índice fuera del tablero se rechaza sin tocar nada.
        /// </summary>
        public RevealResult Reveal(int index)
        {
            if (Phase == MatchPhase.Finished)
            {
                return new RevealResult(RevealOutcome.MatchFinished, CurrentPlayer, CurrentPlayer, 0, 0, true, Array.Empty<int>());
            }

            if (index < 0 || index >= board.CellCount)
            {
                return new RevealResult(RevealOutcome.InvalidCell, CurrentPlayer, CurrentPlayer, 0, LivesOf(CurrentPlayer), false, Array.Empty<int>());
            }

            if (board.IsRevealed(index))
            {
                return new RevealResult(RevealOutcome.AlreadyRevealed, CurrentPlayer, CurrentPlayer, 0, LivesOf(CurrentPlayer), false, Array.Empty<int>());
            }

            Player who = CurrentPlayer;
            var cells = board.Reveal(index);
            bool mine = board.IsMine(index);
            int lost = mine ? 1 : 0;

            if (mine)
            {
                ApplyLifeLoss(who);
            }

            bool ended = Phase == MatchPhase.Finished;
            if (!ended)
            {
                CurrentPlayer = Other(who);
            }

            TurnNumber++;

            return new RevealResult(
                mine ? RevealOutcome.Mine : RevealOutcome.Safe,
                who,
                CurrentPlayer,
                lost,
                LivesOf(who),
                ended,
                cells);
        }

        /// <summary>
        /// Pasa al siguiente nivel. Normalmente solo puede hacerlo el jugador
        /// responsable del nivel actual. Excepción: si el tablero ya está limpio
        /// no queda nada que revelar, así que se permite avanzar a cualquiera;
        /// de lo contrario, si el rival limpia la última casilla y le tocara
        /// pasar, la partida quedaría bloqueada sin forma de continuar. Si quedan
        /// casillas seguras ocultas, se penaliza con una vida. Si al llegar a 0
        /// vidas la partida termina de inmediato.
        /// </summary>
        public AdvanceResult AdvanceLevel()
        {
            if (Phase == MatchPhase.Finished)
            {
                return new AdvanceResult(AdvanceOutcome.MatchFinished, Level, 0, 0, false, true);
            }

            if (!IsCurrentPlayerLevelOwner && !board.IsCleared)
            {
                return new AdvanceResult(AdvanceOutcome.NotLevelOwner, Level, 0, LivesOf(CurrentPlayer), false, false);
            }

            int hidden = board.RemainingSafeCells;
            int lost = hidden > 0 ? 1 : 0;
            Player who = CurrentPlayer;

            if (lost > 0)
            {
                ApplyLifeLoss(who);
            }

            bool ended = Phase == MatchPhase.Finished;
            if (!ended)
            {
                Level++;
                board = BuildBoard(Level);
                CurrentPlayer = LevelOwnerFor(Level);
            }

            TurnNumber++;

            return new AdvanceResult(
                AdvanceOutcome.Advanced,
                Level,
                lost,
                LivesOf(who),
                lost > 0,
                ended);
        }

        /// <summary>
        /// El jugador al que le toca agota su tiempo: pierde una vida y el turno pasa
        /// al rival, igual que si hubiera pisado una mina. Es una decisión de diseño
        /// del equipo; está aquí para que el servidor la aplique sin inventar reglas.
        /// </summary>
        public RevealResult ExpireTurn()
        {
            if (Phase == MatchPhase.Finished)
            {
                return new RevealResult(RevealOutcome.MatchFinished, CurrentPlayer, CurrentPlayer, 0, 0, true, Array.Empty<int>());
            }

            Player who = CurrentPlayer;
            ApplyLifeLoss(who);

            bool ended = Phase == MatchPhase.Finished;
            if (!ended)
            {
                CurrentPlayer = Other(who);
            }

            TurnNumber++;

            return new RevealResult(RevealOutcome.TimedOut, who, CurrentPlayer, 1, LivesOf(who), ended, Array.Empty<int>());
        }

        /// <summary>El jugador abandona: pierde todas sus vidas y la partida termina.</summary>
        public void Forfeit(Player loser)
        {
            if (Phase == MatchPhase.Finished)
            {
                return;
            }

            if (loser == Player.PlayerOne)
            {
                playerOneLives = 0;
            }
            else
            {
                playerTwoLives = 0;
            }

            Phase = MatchPhase.Finished;
            TurnNumber++;
        }

        public static Player Other(Player player)
        {
            return player == Player.PlayerOne ? Player.PlayerTwo : Player.PlayerOne;
        }

        /// <summary>
        /// El tablero de cada nivel sale solo de (semilla de la partida, nivel): así el
        /// replay es determinista y no depende del orden en que se fueron generando los
        /// niveles.
        /// </summary>
        private Board BuildBoard(int level)
        {
            LevelDefinition def = LevelTable.For(level);
            var random = new Random(unchecked(matchSeed * 486187739 + level * 16777619));
            int mines = random.Next(def.MinMines, def.MaxMines + 1);
            return new Board(def.Size, mines, random.Next());
        }

        private void ApplyLifeLoss(Player player)
        {
            if (player == Player.PlayerOne)
            {
                playerOneLives--;
            }
            else
            {
                playerTwoLives--;
            }

            if (LivesOf(player) <= 0)
            {
                Phase = MatchPhase.Finished;
            }
        }
    }
}
