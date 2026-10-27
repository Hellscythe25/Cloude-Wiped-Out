namespace CloudGame.Online
{
    /// <summary>
    /// Versión del contrato de los RPC (parámetros y forma de la vista). Gemelo de
    /// <c>CloudCode.Modules/WipedOut/Project/GameProtocol.cs</c>: el módulo no comparte este
    /// archivo, así que hay que subir las DOS constantes a la vez cada vez que cambie un RPC.
    /// El servidor pone su versión en cada vista; si no coincide con esta, el cliente no la
    /// interpreta y avisa al jugador de que actualice el juego.
    /// </summary>
    public static class GameProtocol
    {
        public const int Version = 1;
    }
}
