namespace WipedOut;

/// <summary>
/// Versión del contrato de los RPC (parámetros y forma de la vista). Es una constante simple y
/// tiene un gemelo en el cliente: <c>Assets/Scripts/Online/GameProtocol.cs</c>. No se comparte
/// como código fuente (el módulo solo compila <c>Assets/Scripts/Gameplay</c>), así que hay que
/// subir las DOS a la vez cada vez que cambie un RPC. Cada vista que envía el servidor lleva
/// esta versión; si el cliente ve otra distinta, avisa al jugador de que actualice el juego en
/// lugar de interpretar mal la respuesta.
/// </summary>
public static class GameProtocol
{
    public const int Version = 1;
}
