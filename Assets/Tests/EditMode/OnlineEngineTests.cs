using System.Linq;
using CloudGame.Gameplay;
using NUnit.Framework;

namespace CloudGame.Tests.EditMode
{
    /// <summary>Lo que el motor necesita para que un servidor pueda ejecutarlo: estado guardable y entradas validadas.</summary>
    public class OnlineEngineTests
    {
        private static int First(MatchEngine match, bool mine)
        {
            for (int i = 0; i < match.Board.CellCount; i++)
            {
                if (!match.Board.IsRevealed(i) && match.Board.IsMine(i) == mine)
                {
                    return i;
                }
            }

            return -1;
        }

        [Test]
        public void ElMismoSeedDaLaMismaPartidaCompleta()
        {
            var a = new MatchEngine(seed: 77);
            var b = new MatchEngine(seed: 77);

            a.AdvanceLevel();
            b.AdvanceLevel();

            Assert.That(a.Board.MineIndices(), Is.EqualTo(b.Board.MineIndices()));
        }

        [Test]
        public void UnIndiceFueraDelTableroSeRechazaSinLanzarNiCambiarNada()
        {
            var match = new MatchEngine(seed: 1);

            Assert.That(match.Reveal(-1).Outcome, Is.EqualTo(RevealOutcome.InvalidCell));
            Assert.That(match.Reveal(match.Board.CellCount).Outcome, Is.EqualTo(RevealOutcome.InvalidCell));
            Assert.That(match.TurnNumber, Is.EqualTo(0));
            Assert.That(match.CurrentPlayer, Is.EqualTo(Player.PlayerOne));
        }

        [Test]
        public void ElNumeroDeTurnoSoloSubeConAccionesAplicadas()
        {
            var match = new MatchEngine(seed: 2);
            int safe = First(match, mine: false);

            match.Reveal(safe);
            match.Reveal(safe);           // ya revelada: se rechaza

            Assert.That(match.TurnNumber, Is.EqualTo(1));
        }

        [Test]
        public void AgotarElTurnoRestaUnaVidaYPasaElTurno()
        {
            var match = new MatchEngine(seed: 3);

            var result = match.ExpireTurn();

            Assert.That(result.Outcome, Is.EqualTo(RevealOutcome.TimedOut));
            Assert.That(match.PlayerOneLives, Is.EqualTo(2));
            Assert.That(match.CurrentPlayer, Is.EqualTo(Player.PlayerTwo));
            Assert.That(match.TurnNumber, Is.EqualTo(1));
        }

        [Test]
        public void AgotarElTurnoConUnaVidaTerminaLaPartida()
        {
            var match = new MatchEngine(seed: 4);
            match.ExpireTurn();   // J1 -> 2 vidas
            match.ExpireTurn();   // J2 -> 2 vidas
            match.ExpireTurn();   // J1 -> 1
            match.ExpireTurn();   // J2 -> 1

            var last = match.ExpireTurn();   // J1 -> 0

            Assert.That(last.MatchEnded, Is.True);
            Assert.That(match.Phase, Is.EqualTo(MatchPhase.Finished));
            Assert.That(match.Winner, Is.EqualTo(Player.PlayerTwo));
        }

        [Test]
        public void AbandonarTerminaLaPartidaYGanaElRival()
        {
            var match = new MatchEngine(seed: 5);

            match.Forfeit(Player.PlayerTwo);

            Assert.That(match.Phase, Is.EqualTo(MatchPhase.Finished));
            Assert.That(match.Winner, Is.EqualTo(Player.PlayerOne));
        }

        /// <summary>Aplica una lista de jugadas a un motor nuevo: lo que hace el servidor en cada llamada.</summary>
        private static MatchEngine Replay(int seed, params int[] cellsToReveal)
        {
            var match = new MatchEngine(seed);
            foreach (int cell in cellsToReveal)
            {
                match.Reveal(cell);
            }

            return match;
        }

        [Test]
        public void LaMismaSemillaYLasMismasJugadasDanElMismoEstado()
        {
            var original = new MatchEngine(seed: 6);
            var moves = new[] { First(original, mine: false), -1, -1 };
            original.Reveal(moves[0]);
            moves[1] = First(original, mine: true);
            original.Reveal(moves[1]);
            moves[2] = First(original, mine: false);
            original.Reveal(moves[2]);

            var replayed = Replay(6, moves);

            Assert.That(replayed.TurnNumber, Is.EqualTo(original.TurnNumber));
            Assert.That(replayed.CurrentPlayer, Is.EqualTo(original.CurrentPlayer));
            Assert.That(replayed.PlayerOneLives, Is.EqualTo(original.PlayerOneLives));
            Assert.That(replayed.PlayerTwoLives, Is.EqualTo(original.PlayerTwoLives));
            Assert.That(replayed.Board.PublicCells(), Is.EqualTo(original.Board.PublicCells()));
            Assert.That(replayed.Board.MineIndices(), Is.EqualTo(original.Board.MineIndices()));
        }

        [Test]
        public void ElReplayTambienReproduceElCambioDeNivel()
        {
            var original = new MatchEngine(seed: 8);
            original.AdvanceLevel();   // J1 es dueño del nivel 1 y avanza (-1 vida); el nivel 2 es de J2
            original.Reveal(First(original, mine: false));

            var replayed = new MatchEngine(8);
            replayed.AdvanceLevel();
            replayed.Reveal(First(replayed, mine: false));

            Assert.That(replayed.Level, Is.EqualTo(2));
            Assert.That(replayed.Board.MineIndices(), Is.EqualTo(original.Board.MineIndices()));
            Assert.That(replayed.Board.PublicCells(), Is.EqualTo(original.Board.PublicCells()));
        }

        [Test]
        public void SemillasDistintasDanTablerosDistintos()
        {
            var a = new MatchEngine(seed: 100);
            var b = new MatchEngine(seed: 101);

            Assert.That(a.Board.MineIndices(), Is.Not.EqualTo(b.Board.MineIndices()));
        }

        [Test]
        public void LaVistaPublicaOcultaTodasLasMinasNoReveladas()
        {
            var match = new MatchEngine(seed: 10);
            match.Reveal(First(match, mine: true));

            int[] cells = match.Board.PublicCells();

            Assert.That(cells.Count(c => c == Board.MineCell), Is.EqualTo(1), "solo la pisada");
            for (int i = 0; i < cells.Length; i++)
            {
                if (!match.Board.IsRevealed(i))
                {
                    Assert.That(cells[i], Is.EqualTo(Board.HiddenCell));
                }
            }
        }
    }
}
