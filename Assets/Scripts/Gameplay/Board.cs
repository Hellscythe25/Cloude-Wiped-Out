using System;
using System.Collections.Generic;

namespace CloudGame.Gameplay
{
    /// <summary>
    /// Tablero de minas. Modelo puro, sin dependencias de Unity, con mines
    /// colocadas por semilla para que las partidas sean reproducibles.
    /// </summary>
    public sealed class Board : IBoardReadModel
    {
        /// <summary>Valor de <see cref="PublicCells"/> para una celda oculta.</summary>
        public const int HiddenCell = -1;

        /// <summary>Valor de <see cref="PublicCells"/> para una mina ya revelada.</summary>
        public const int MineCell = -2;

        private readonly bool[] mines;
        private readonly bool[] revealed;
        private readonly int[] adjacentMines;
        private readonly List<int> revealedThisMove = new List<int>();

        public Board(int size, int mineCount, int seed)
        {
            if (size < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(size), "El tablero mínimo es 2x2.");
            }

            int cells = size * size;
            if (mineCount < 1 || mineCount > cells - 1)
            {
                throw new ArgumentOutOfRangeException(nameof(mineCount), "Debe quedar al menos una casilla segura.");
            }

            Size = size;
            Seed = seed;
            mines = new bool[cells];
            revealed = new bool[cells];
            adjacentMines = new int[cells];

            PlaceMines(mineCount, seed);
            ComputeAdjacent();
        }

        public int Size { get; }

        public int Seed { get; }

        public int CellCount => Size * Size;

        public int MineCount { get; private set; }

        public int RevealedCount { get; private set; }

        public bool IsCleared => RevealedCount >= CellCount - MineCount;

        public bool InBounds(int x, int y)
        {
            return x >= 0 && x < Size && y >= 0 && y < Size;
        }

        public int Index(int x, int y) => y * Size + x;

        public bool IsMine(int index) => mines[index];

        public bool IsRevealed(int index) => revealed[index];

        public int AdjacentMines(int index) => adjacentMines[index];

        /// <summary>
        /// Revela una casilla. Si es mina solo se marca esa casilla; si es segura y
        /// no tiene minas alrededor, arrastra en cascada sus vecinas seguras.
        /// </summary>
        public IReadOnlyList<int> Reveal(int index)
        {
            if (index < 0 || index >= CellCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            if (revealed[index])
            {
                return Array.Empty<int>();
            }

            revealedThisMove.Clear();
            FloodFill(index);
            return revealedThisMove;
        }

        /// <summary>
        /// Vista que el servidor puede enseñar a los clientes, una entrada por celda:
        /// <see cref="HiddenCell"/> si sigue oculta, <see cref="MineCell"/> si es una
        /// mina ya pisada, o el número de minas vecinas (0-8) si es una celda segura
        /// revelada. Las minas ocultas nunca aparecen aquí.
        /// </summary>
        public int[] PublicCells()
        {
            var result = new int[CellCount];
            for (int i = 0; i < result.Length; i++)
            {
                if (!revealed[i])
                {
                    result[i] = HiddenCell;
                }
                else
                {
                    result[i] = mines[i] ? MineCell : adjacentMines[i];
                }
            }

            return result;
        }

        /// <summary>Índices de todas las minas. Solo debe usarse cuando la partida ha terminado.</summary>
        public int[] MineIndices()
        {
            var result = new List<int>(MineCount);
            for (int i = 0; i < CellCount; i++)
            {
                if (mines[i])
                {
                    result.Add(i);
                }
            }

            return result.ToArray();
        }

        /// <summary>Celdas seguras todavía sin revelar, que son las que hay que limpiar.</summary>
        public int RemainingSafeCells
        {
            get
            {
                int remaining = 0;
                for (int i = 0; i < CellCount; i++)
                {
                    if (!revealed[i] && !mines[i])
                    {
                        remaining++;
                    }
                }

                return remaining;
            }
        }

        private void PlaceMines(int mineCount, int seed)
        {
            var random = new Random(seed);
            var pool = new List<int>(CellCount);
            for (int i = 0; i < CellCount; i++)
            {
                pool.Add(i);
            }

            for (int i = 0; i < mineCount; i++)
            {
                int pick = random.Next(i, pool.Count);
                (pool[i], pool[pick]) = (pool[pick], pool[i]);
                mines[pool[i]] = true;
            }

            MineCount = mineCount;
        }

        private void ComputeAdjacent()
        {
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int count = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0)
                            {
                                continue;
                            }

                            int nx = x + dx;
                            int ny = y + dy;
                            if (InBounds(nx, ny) && mines[Index(nx, ny)])
                            {
                                count++;
                            }
                        }
                    }

                    adjacentMines[Index(x, y)] = count;
                }
            }
        }

        private void FloodFill(int start)
        {
            var stack = new Stack<int>();
            stack.Push(start);

            while (stack.Count > 0)
            {
                int index = stack.Pop();
                if (revealed[index])
                {
                    continue;
                }

                revealed[index] = true;
                revealedThisMove.Add(index);
                RevealedCount++;

                if (mines[index] || adjacentMines[index] > 0)
                {
                    continue;
                }

                int x = index % Size;
                int y = index / Size;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                        {
                            continue;
                        }

                        int nx = x + dx;
                        int ny = y + dy;
                        if (InBounds(nx, ny))
                        {
                            int n = Index(nx, ny);
                            if (!revealed[n] && !mines[n])
                            {
                                stack.Push(n);
                            }
                        }
                    }
                }
            }
        }
    }
}
