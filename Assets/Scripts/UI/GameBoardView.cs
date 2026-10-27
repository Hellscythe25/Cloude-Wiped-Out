using System.Collections.Generic;
using CloudGame.Gameplay;
using UnityEngine;
using UnityEngine.UI;

namespace CloudGame.UI
{
    /// <summary>
    /// Dibuja el tablero de <see cref="Board"/> como una rejilla de celdas UGUI.
    /// Las minas están ocultas durante la partida; solo se muestran todas al
    /// terminar, para que el tablero no regale la solución.
    /// </summary>
    public sealed class GameBoardView : MonoBehaviour
    {
        private static readonly Color HiddenColor = new Color(0.85f, 0.72f, 0.55f);
        private static readonly Color RevealedColor = new Color(0.98f, 0.94f, 0.87f);
        private static readonly Color MineColor = new Color(0.85f, 0.30f, 0.25f);
        private static readonly Color BorderColor = new Color(0.60f, 0.46f, 0.34f);

        private static readonly Color[] NumberColors =
        {
            new Color(0.25f, 0.45f, 0.85f),
            new Color(0.30f, 0.60f, 0.30f),
            new Color(0.85f, 0.30f, 0.25f),
            new Color(0.35f, 0.25f, 0.60f),
            new Color(0.60f, 0.30f, 0.15f),
            new Color(0.20f, 0.60f, 0.60f),
            new Color(0.15f, 0.15f, 0.20f),
            new Color(0.50f, 0.50f, 0.50f),
        };

        [SerializeField] private RectTransform grid;
        [SerializeField] private Font font;
        [SerializeField] private float cellSize = 88f;
        [SerializeField] private float spacing = 8f;

        private readonly List<CellView> cells = new List<CellView>();
        private GameScreenController controller;
        private System.Action<int> cellClicked;

        public void Initialize(GameScreenController owner)
        {
            controller = owner;
        }

        /// <summary>
        /// Alternativa para quien no es el <see cref="GameScreenController"/> local (por
        /// ejemplo la pantalla online): recibe la pulsación como un callback.
        /// </summary>
        public void Initialize(System.Action<int> onCellClicked)
        {
            cellClicked = onCellClicked;
        }

        /// <summary>
        /// Para crear la vista desde código en lugar de desde una escena: indica dónde se
        /// dibuja la rejilla y con qué fuente.
        /// </summary>
        public void Configure(RectTransform gridRoot, Font textFont)
        {
            grid = gridRoot;
            font = textFont;
        }

        /// <summary>Tamaño de celda y separación. Hay que llamarlo antes de <see cref="Build"/>.</summary>
        public void SetLayout(float newCellSize, float newSpacing)
        {
            cellSize = newCellSize;
            spacing = newSpacing;
        }

        /// <summary>Recrea la rejilla para el tamaño del tablero indicado.</summary>
        public void Build(IBoardReadModel board)
        {
            if (grid == null)
            {
                return;
            }

            foreach (CellView cell in cells)
            {
                if (cell.Root != null)
                {
                    // Se desactivan antes de destruirse para que no lleguen a
                    // dibujarse ni los cuente la rejilla en este frame.
                    cell.Root.gameObject.SetActive(false);

                    if (Application.isPlaying)
                    {
                        Destroy(cell.Root.gameObject);
                    }
                    else
                    {
                        DestroyImmediate(cell.Root.gameObject);
                    }
                }
            }

            cells.Clear();

            GridLayoutGroup layout = grid.GetComponent<GridLayoutGroup>();
            if (layout == null)
            {
                layout = grid.gameObject.AddComponent<GridLayoutGroup>();
            }

            layout.cellSize = new Vector2(cellSize, cellSize);
            layout.spacing = new Vector2(spacing, spacing);
            layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            layout.constraintCount = board.Size;
            layout.childAlignment = TextAnchor.MiddleCenter;

            for (int i = 0; i < board.CellCount; i++)
            {
                cells.Add(CreateCell(i));
            }
        }

        /// <summary>
        /// Pinta el estado actual. <paramref name="revealMines"/> muestra todas
        /// las minas, y solo se usa cuando la partida ha terminado.
        /// </summary>
        public void Paint(IBoardReadModel board, bool revealMines)
        {
            for (int i = 0; i < cells.Count && i < board.CellCount; i++)
            {
                PaintCell(cells[i], board, i, revealMines);
            }
        }

        private void PaintCell(CellView cell, IBoardReadModel board, int index, bool revealMines)
        {
            bool revealed = board.IsRevealed(index);
            bool mine = board.IsMine(index);

            if (revealed)
            {
                cell.Image.color = mine ? MineColor : RevealedColor;
                cell.Border.color = mine ? MineColor : BorderColor;
            }
            else if (revealMines && mine)
            {
                cell.Image.color = MineColor;
                cell.Border.color = MineColor;
            }
            else
            {
                cell.Image.color = HiddenColor;
                cell.Border.color = BorderColor;
            }

            int adjacent = board.AdjacentMines(index);
            if (revealed && !mine)
            {
                cell.Label.text = adjacent > 0 ? adjacent.ToString() : string.Empty;
                cell.Label.color = adjacent > 0 && adjacent <= NumberColors.Length
                    ? NumberColors[adjacent - 1]
                    : Color.white;
            }
            else if (revealed && mine)
            {
                cell.Label.text = "X";
                cell.Label.color = Color.white;
            }
            else
            {
                cell.Label.text = string.Empty;
            }
        }

        private CellView CreateCell(int index)
        {
            var root = new GameObject("Cell_" + index, typeof(RectTransform));
            root.transform.SetParent(grid, false);
            var rect = (RectTransform)root.transform;
            rect.sizeDelta = new Vector2(cellSize, cellSize);

            var border = new GameObject("Border", typeof(RectTransform));
            border.transform.SetParent(root.transform, false);
            var borderRect = (RectTransform)border.transform;
            borderRect.anchorMin = Vector2.zero;
            borderRect.anchorMax = Vector2.one;
            borderRect.offsetMin = Vector2.zero;
            borderRect.offsetMax = Vector2.zero;
            Image borderImage = border.AddComponent<Image>();
            borderImage.color = BorderColor;
            borderImage.raycastTarget = false;

            var inner = new GameObject("Fill", typeof(RectTransform));
            inner.transform.SetParent(border.transform, false);
            var innerRect = (RectTransform)inner.transform;
            innerRect.anchorMin = Vector2.zero;
            innerRect.anchorMax = Vector2.one;
            innerRect.offsetMin = new Vector2(3f, 3f);
            innerRect.offsetMax = new Vector2(-3f, -3f);
            Image fillImage = inner.AddComponent<Image>();
            fillImage.color = HiddenColor;

            var label = new GameObject("Label", typeof(RectTransform));
            label.transform.SetParent(inner.transform, false);
            var labelRect = (RectTransform)label.transform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            Text labelText = label.AddComponent<Text>();
            labelText.font = font;
            labelText.fontSize = 36;
            labelText.alignment = TextAnchor.MiddleCenter;
            labelText.raycastTarget = false;
            labelText.color = Color.white;

            Button button = root.AddComponent<Button>();
            button.targetGraphic = fillImage;

            int captured = index;
            button.onClick.AddListener(() => OnCellClicked(captured));

            return new CellView
            {
                Root = rect,
                Image = fillImage,
                Border = borderImage,
                Label = labelText,
            };
        }

        private void OnCellClicked(int index)
        {
            if (cellClicked != null)
            {
                cellClicked(index);
            }
            else if (controller != null)
            {
                controller.OnCellClicked(index);
            }
        }

        private sealed class CellView
        {
            public RectTransform Root;
            public Image Image;
            public Image Border;
            public Text Label;
        }
    }
}
