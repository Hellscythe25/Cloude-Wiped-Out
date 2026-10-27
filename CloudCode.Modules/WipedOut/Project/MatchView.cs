namespace WipedOut;

/// <summary>
/// Lo ÚNICO que ve un cliente. Se construye siempre desde cero a partir del estado:
/// no tiene ningún campo con la semilla ni con las minas ocultas. Las minas solo
/// aparecen en <see cref="Mines"/> cuando la partida ya terminó.
///
/// Se serializa en camelCase (ver <see cref="MatchJson"/>) y se envía como texto JSON,
/// tanto en la respuesta de cada función como en los mensajes push.
/// </summary>
public class MatchView
{
    /// <summary>Versión del contrato (ver <see cref="GameProtocol"/>). El cliente la compara con la suya.</summary>
    public int ProtocolVersion { get; set; } = GameProtocol.Version;

    public string MatchCode { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    /// <summary>Asiento de quien recibe la vista: 1 o 2.</summary>
    public int MySeat { get; set; }

    public int TurnNumber { get; set; }

    public int Level { get; set; }

    public int Size { get; set; }

    /// <summary>
    /// Una entrada por celda: -1 oculta, -2 mina ya pisada, 0-8 celda segura revelada
    /// con ese número de minas vecinas. Vacía mientras la sala espera al rival.
    /// </summary>
    public int[] Cells { get; set; } = Array.Empty<int>();

    public int Lives1 { get; set; }

    public int Lives2 { get; set; }

    /// <summary>Asiento al que le toca jugar (0 si la partida no está en curso).</summary>
    public int CurrentSeat { get; set; }

    public int LevelOwnerSeat { get; set; }

    public bool IsCleared { get; set; }

    public string TurnDeadlineUtc { get; set; } = string.Empty;

    public string ServerTimeUtc { get; set; } = string.Empty;

    /// <summary>Resultado de la petición que originó esta vista (ver <see cref="MatchOutcome"/>).</summary>
    public string Outcome { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public LastEvent? LastEvent { get; set; }

    public int WinnerSeat { get; set; }

    public string EndReason { get; set; } = string.Empty;

    /// <summary>Solo con la partida terminada. En curso es null y ni siquiera se serializa.</summary>
    public int[]? Mines { get; set; }
}
