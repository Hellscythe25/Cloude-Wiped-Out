using System.Linq;
using CloudGame.Gameplay;
using NUnit.Framework;

namespace CloudGame.Tests.EditMode
{
    public class LevelTableTests
    {
        [Test]
        public void PrimerosCincoNivosCumplenElDisenoAcordado()
        {
            Assert.That(LevelTable.For(1).Size, Is.EqualTo(2));
            Assert.That(LevelTable.For(1).MineCountIsValid(2), Is.True);

            Assert.That(LevelTable.For(2).Size, Is.EqualTo(3));
            Assert.That(LevelTable.For(3).Size, Is.EqualTo(3));
            Assert.That(LevelTable.For(4).Size, Is.EqualTo(4));
            Assert.That(LevelTable.For(5).Size, Is.EqualTo(4));
        }

        [Test]
        public void ElTiempoPorTurnoBajaSegunAvanzaElNivel()
        {
            Assert.That(LevelTable.For(1).TurnSeconds, Is.EqualTo(5f));
            Assert.That(LevelTable.For(3).TurnSeconds, Is.EqualTo(4f));
            Assert.That(LevelTable.For(5).TurnSeconds, Is.EqualTo(3f));
        }

        [Test]
        public void NumeroDeMinasCreceConElNivel()
        {
            float min1 = LevelTable.For(1).MinMines;
            float max5 = LevelTable.For(5).MaxMines;
            Assert.That(max5, Is.GreaterThan(min1));
        }

        [Test]
        public void TodosLosNiviosDejanAlMenosUnaCasillaSegura()
        {
            for (int level = 1; level <= 40; level++)
            {
                LevelDefinition def = LevelTable.For(level);
                Assert.That(def.MaxMines, Is.LessThan(def.CellCount), $"Nivel {level} no deja casilla segura");
                Assert.That(def.MinMines, Is.GreaterThanOrEqualTo(1), $"Nivel {level} necesita al menos una mina");
                Assert.That(def.MinMines, Is.LessThanOrEqualTo(def.MaxMines), $"Nivel {level} con rango de minas invertido");
            }
        }

        [Test]
        public void NivelesAltosSiguenEscalandoElTableroYQuedanAcotados()
        {
            Assert.That(LevelTable.For(40).Size, Is.LessThanOrEqualTo(12));
            Assert.That(LevelTable.For(40).Size, Is.GreaterThan(LevelTable.For(12).Size));
        }

        [Test]
        public void NivelesNoPositivosSeTratanComoElPrimero()
        {
            Assert.That(LevelTable.For(0).Size, Is.EqualTo(LevelTable.For(1).Size));
            Assert.That(LevelTable.For(-5).Size, Is.EqualTo(LevelTable.For(1).Size));
        }
    }

    internal static class LevelDefinitionTestExtensions
    {
        public static bool MineCountIsValid(this LevelDefinition def, int count)
        {
            return count >= def.MinMines && count <= def.MaxMines;
        }
    }

    public class BoardTests
    {
        [Test]
        public void RespetaElNumeroDeMinasSolicitado()
        {
            var board = new Board(5, 7, seed: 1234);
            int mines = Enumerable.Range(0, board.CellCount).Count(board.IsMine);
            Assert.That(mines, Is.EqualTo(7));
        }

        [Test]
        public void LaMismaSemillaGeneraElMismoTablero()
        {
            var a = new Board(6, 9, seed: 99);
            var b = new Board(6, 9, seed: 99);

            for (int i = 0; i < a.CellCount; i++)
            {
                Assert.That(b.IsMine(i), Is.EqualTo(a.IsMine(i)), $"difieren en la casilla {i}");
                Assert.That(b.AdjacentMines(i), Is.EqualTo(a.AdjacentMines(i)), $"difieren en vecinas de {i}");
            }
        }

        [Test]
        public void CuentaBienLasMinasVecinas()
        {
            // 3x3 con una sola mina: las ocho casillas de alrededor deben ver 1
            // y la propia mina 0. No fijamos dónde cae la mina, así que el test
            // no depende de la semilla.
            var board = new Board(3, 1, seed: 0);
            int mine = Enumerable.Range(0, board.CellCount).Single(board.IsMine);
            int mx = mine % 3;
            int my = mine / 3;

            Assert.That(board.AdjacentMines(mine), Is.EqualTo(0));

            for (int i = 0; i < board.CellCount; i++)
            {
                if (i == mine)
                {
                    continue;
                }

                int x = i % 3;
                int y = i / 3;
                bool vecinas = System.Math.Abs(x - mx) <= 1 && System.Math.Abs(y - my) <= 1;
                Assert.That(
                    board.AdjacentMines(i),
                    Is.EqualTo(vecinas ? 1 : 0),
                    $"casilla {i} (x={x}, y={y}) respecto a la mina en ({mx}, {my})");
            }
        }

        [Test]
        public void RevelarUnaCasillaConMinasAlrededorNoArrastra()
        {
            // 2x2 con una mina: las otras tres casillas tienen exactamente una
            // mina al lado, así que revelar una no debe abrir en cascada.
            var board = new Board(2, 1, seed: 0);
            int conVecinas = Enumerable.Range(0, board.CellCount).First(i => !board.IsMine(i));

            Assert.That(board.AdjacentMines(conVecinas), Is.EqualTo(1));

            var cells = board.Reveal(conVecinas);
            Assert.That(cells.Count, Is.EqualTo(1));
        }

        [Test]
        public void RevelarUnaCasillaLimpiaArrastraEnCascada()
        {
            var board = new Board(5, 2, seed: 7);
            int limpia = Enumerable.Range(0, board.CellCount)
                .First(i => !board.IsMine(i) && board.AdjacentMines(i) == 0);

            var cells = board.Reveal(limpia);
            Assert.That(cells.Count, Is.GreaterThan(1));
        }

        [Test]
        public void NoSePuedeRevelarDosVecesLaMismaCasilla()
        {
            var board = new Board(4, 4, seed: 3);
            int cell = Enumerable.Range(0, board.CellCount).First(i => !board.IsMine(i));

            Assert.That(board.Reveal(cell).Count, Is.GreaterThan(0));
            Assert.That(board.Reveal(cell).Count, Is.EqualTo(0));
        }

        [Test]
        public void ElTableroEstaLimpioCuandoSoloQuedanMinasOcultas()
        {
            var board = new Board(3, 2, seed: 11);

            for (int i = 0; i < board.CellCount && !board.IsCleared; i++)
            {
                if (!board.IsMine(i))
                {
                    board.Reveal(i);
                }
            }

            Assert.That(board.IsCleared, Is.True);
            Assert.That(board.RemainingSafeCells, Is.EqualTo(0));
        }

        [Test]
        public void CuentaLasCasillasSegurasOcultas()
        {
            var board = new Board(4, 5, seed: 21);
            int esperado = 16 - 5;
            Assert.That(board.RemainingSafeCells, Is.EqualTo(esperado));

            board.Reveal(Enumerable.Range(0, board.CellCount).First(i => !board.IsMine(i)));
            Assert.That(board.RemainingSafeCells, Is.LessThan(esperado));
        }
    }
}
