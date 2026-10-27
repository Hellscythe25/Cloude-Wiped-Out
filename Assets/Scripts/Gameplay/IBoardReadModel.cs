namespace CloudGame.Gameplay
{
    /// <summary>
    /// Lo mínimo que necesita una vista para dibujar un tablero. <see cref="Board"/>
    /// lo implementa con toda la verdad; <c>PublicBoard</c> (cliente online) lo
    /// implementa solo con lo que el servidor decidió mostrar, para que el cliente
    /// nunca llegue a conocer las minas ocultas.
    /// </summary>
    public interface IBoardReadModel
    {
        int Size { get; }

        int CellCount { get; }

        bool IsRevealed(int index);

        bool IsMine(int index);

        int AdjacentMines(int index);
    }
}
