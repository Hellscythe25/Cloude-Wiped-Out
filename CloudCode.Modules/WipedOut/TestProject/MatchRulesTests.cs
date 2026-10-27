using System.Globalization;
using CloudGame.Gameplay;
using Newtonsoft.Json.Linq;
using WipedOut;

namespace WipedOut.Tests;

/// <summary>
/// Pruebas de las reglas de la sala online. Corren sin servidor ni Unity porque
/// <see cref="MatchRules"/> no sabe que existen Cloud Code ni Cloud Save.
/// </summary>
public class MatchRulesTests
{
    private const string Host = "player-host";
    private const string Guest = "player-guest";
    private const int Seed = 1234;

    private static readonly DateTime T0 = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private static MatchState Waiting() => MatchRules.NewMatch("ABCD", Host, "req-create", T0);

    private static MatchState Playing()
    {
        var state = Waiting();
        MatchRules.StartWithGuest(state, Guest, Seed, T0);
        return state;
    }

    private static MatchEngine EngineOf(MatchState state) => MatchRules.Replay(state).Engine!;

    private static DateTime At(double seconds) => T0.AddSeconds(seconds);

    private static MoveResult Reveal(MatchState s, string who, string req, int turn, int cell, double t) =>
        MatchRules.ApplyPlay(s, who, req, turn, MoveKind.Reveal, cell, At(t));

    private static MoveResult Advance(MatchState s, string who, string req, int turn, double t) =>
        MatchRules.ApplyPlay(s, who, req, turn, MoveKind.Advance, -1, At(t));

    private static MoveResult Forfeit(MatchState s, string who, string req, double t) =>
        MatchRules.ApplyPlay(s, who, req, 0, MoveKind.Forfeit, -1, At(t));

    private static MatchView ViewFor(MatchState s, string who) =>
        MatchRules.BuildView(s, who, MatchOutcome.Applied, "", T0);

    private static DateTime Deadline(MatchView view) =>
        DateTime.Parse(view.TurnDeadlineUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    /// <summary>Busca una casilla sin revelar (segura o mina) mirando el estado SECRETO del servidor.</summary>
    private static int Find(MatchState state, bool mine)
    {
        var board = EngineOf(state).Board;
        for (var i = 0; i < board.CellCount; i++)
        {
            if (!board.IsRevealed(i) && board.IsMine(i) == mine) return i;
        }

        return -1;
    }

    // ---------------------------------------------------------------- arranque

    [Test]
    public void UnaSalaRecienCreadaEsperaAlRival()
    {
        var view = ViewFor(Waiting(), Host);

        Assert.That(view.Status, Is.EqualTo(MatchStatus.WaitingForGuest));
        Assert.That(view.MySeat, Is.EqualTo(1));
        Assert.That(view.Cells, Is.Empty, "todavía no hay tablero");
        Assert.That(view.CurrentSeat, Is.EqualTo(0));
    }

    [Test]
    public void AlUnirseElRivalEmpiezaElNivelUnoConTurnoDelAnfitrion()
    {
        var view = ViewFor(Playing(), Guest);

        Assert.That(view.Status, Is.EqualTo(MatchStatus.Playing));
        Assert.That(view.MySeat, Is.EqualTo(2));
        Assert.That(view.Level, Is.EqualTo(1));
        Assert.That(view.CurrentSeat, Is.EqualTo(1), "el nivel 1 es del Jugador 1");
        Assert.That(view.TurnNumber, Is.EqualTo(0));
        Assert.That(view.Lives1, Is.EqualTo(3));
        Assert.That(view.Lives2, Is.EqualTo(3));
        Assert.That(Deadline(view), Is.EqualTo(T0.AddSeconds(MatchRules.TurnSeconds)));
        Assert.That(view.LastEvent!.Kind, Is.EqualTo("joined"));
    }

    [Test]
    public void LaSemillaNoPuedeSerCeroPorqueElMotorLaTomariaPorAzar()
    {
        Assert.That(() => MatchRules.StartWithGuest(Waiting(), Guest, 0, T0), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    // ------------------------------------------------- información oculta (clave)

    [Test]
    public void LaVistaEnCursoNoContieneLaSemillaNiLasMinasOcultas()
    {
        var json = MatchJson.Serialize(ViewFor(Playing(), Host));

        Assert.That(json, Does.Not.Contain("\"mines\""), "las minas solo se envían al terminar");
        Assert.That(json, Does.Not.Contain("seed").IgnoreCase, "la semilla es secreta");
        Assert.That(json, Does.Not.Contain(Seed.ToString()));
        Assert.That(json, Does.Not.Contain("moves").IgnoreCase, "la lista de jugadas es interna");
    }

    [Test]
    public void SinNadaReveladoNingunaCeldaSeDistingueDeOtra()
    {
        var view = ViewFor(Playing(), Host);

        Assert.That(view.Cells, Is.All.EqualTo(Board.HiddenCell));
    }

    [Test]
    public void SoloLasMinasYaPisadasSeMuestranEnLaVista()
    {
        var state = Playing();
        var mine = Find(state, mine: true);
        Reveal(state, Host, "r1", 0, mine, 1);

        var view = ViewFor(state, Host);

        Assert.That(view.Cells[mine], Is.EqualTo(Board.MineCell));
        Assert.That(view.Cells.Count(c => c == Board.MineCell), Is.EqualTo(1));

        // Las demás minas, que siguen ocultas, no se distinguen de una casilla segura oculta.
        var board = EngineOf(state).Board;
        for (var i = 0; i < board.CellCount; i++)
        {
            if (!board.IsRevealed(i))
            {
                Assert.That(view.Cells[i], Is.EqualTo(Board.HiddenCell), $"celda {i}");
            }
        }
    }

    [Test]
    public void AlTerminarLaPartidaSiSeRevelanTodasLasMinas()
    {
        var state = Playing();
        Forfeit(state, Guest, "r-ff", 1);

        var view = ViewFor(state, Host);

        Assert.That(view.Mines, Is.Not.Null);
        Assert.That(view.Mines!.Length, Is.EqualTo(EngineOf(state).Board.MineCount));
    }

    [Test]
    public void LaVistaSerializaEnCamelCaseYLlevaLaVersionDelProtocolo()
    {
        var json = MatchJson.Serialize(ViewFor(Playing(), Host));

        Assert.That(json, Does.Contain("\"matchCode\""));
        Assert.That(json, Does.Contain("\"turnNumber\""));
        Assert.That(json, Does.Contain("\"cells\""));
        Assert.That(json, Does.Contain($"\"protocolVersion\":{GameProtocol.Version}"));
        Assert.That(json, Does.Not.Contain("\"MatchCode\""));
    }

    [Test]
    public void ElEstadoGuardadoSobreviveAlViajeAJsonYDaLaMismaVista()
    {
        var state = Playing();
        Reveal(state, Host, "r1", 0, Find(state, mine: false), 1);

        // Es lo que hace MatchRepository: JObject.FromObject al guardar, ToObject al leer.
        var restored = JObject.FromObject(state).ToObject<MatchState>()!;

        Assert.That(MatchJson.Serialize(ViewFor(restored, Host)), Is.EqualTo(MatchJson.Serialize(ViewFor(state, Host))));
    }

    // ------------------------------------------------------------ replay (estado)

    [Test]
    public void ElEstadoEsLaSemillaMasLaListaDeJugadasYSeReproduce()
    {
        var state = Playing();
        Reveal(state, Host, "r1", 0, Find(state, mine: false), 1);
        Reveal(state, Guest, "r2", 1, Find(state, mine: true), 2);

        var replayed = MatchRules.Replay(state).Engine!;

        Assert.That(state.Moves.Count, Is.EqualTo(2));
        Assert.That(replayed.TurnNumber, Is.EqualTo(2));
        Assert.That(replayed.PlayerTwoLives, Is.EqualTo(2));
        Assert.That(replayed.CurrentPlayer, Is.EqualTo(Player.PlayerOne));
    }

    [Test]
    public void LaListaDeJugadasEsSoloDeAgregar()
    {
        var state = Playing();
        Reveal(state, Host, "r1", 0, Find(state, mine: false), 1);
        var first = JObject.FromObject(state.Moves[0]).ToString();

        Reveal(state, Guest, "r2", 1, Find(state, mine: false), 2);
        Advance(state, Host, "r3", 2, 3);

        Assert.That(state.Moves.Count, Is.EqualTo(3));
        Assert.That(JObject.FromObject(state.Moves[0]).ToString(), Is.EqualTo(first), "las jugadas anteriores no se tocan");
    }

    [Test]
    public void ElRegistroDeJugadasGuardaQuienQueYCuando()
    {
        var state = Playing();
        var cell = Find(state, mine: false);

        Reveal(state, Host, "mi-jugada", 0, cell, 2);

        var move = state.Moves.Single();
        Assert.That(move.RequestId, Is.EqualTo("mi-jugada"));
        Assert.That(move.Seat, Is.EqualTo(1));
        Assert.That(move.Kind, Is.EqualTo(MoveKind.Reveal));
        Assert.That(move.Cell, Is.EqualTo(cell));
        Assert.That(move.AtUnixSeconds, Is.EqualTo(MatchRules.Unix(At(2))));
    }

    // ---------------------------------------------------------------- jugadas

    [Test]
    public void RevelarUnaCasillaSeguraSeApplicaYPasaElTurno()
    {
        var state = Playing();

        var result = Reveal(state, Host, "r1", 0, Find(state, mine: false), 2);

        Assert.That(result.Outcome, Is.EqualTo(MatchOutcome.Applied));
        Assert.That(result.Changed, Is.True);
        Assert.That(result.GameChanged, Is.True);

        var view = ViewFor(state, Guest);
        Assert.That(view.TurnNumber, Is.EqualTo(1));
        Assert.That(view.CurrentSeat, Is.EqualTo(2));
        Assert.That(view.LastEvent!.Kind, Is.EqualTo(MoveKind.Reveal));
        Assert.That(view.LastEvent.Seat, Is.EqualTo(1));
        Assert.That(Deadline(view), Is.EqualTo(At(2 + MatchRules.TurnSeconds)), "el plazo del siguiente turno cuenta desde la jugada");
    }

    [Test]
    public void PisarUnaMinaRestaUnaVidaYLoDiceLaVista()
    {
        var state = Playing();

        Reveal(state, Host, "r1", 0, Find(state, mine: true), 2);

        var view = ViewFor(state, Host);
        Assert.That(view.Lives1, Is.EqualTo(2));
        Assert.That(view.LastEvent!.WasMine, Is.True);
        Assert.That(view.LastEvent.LivesLost, Is.EqualTo(1));
    }

    [Test]
    public void JugarFueraDeTurnoSeRechazaSinTocarNada()
    {
        var state = Playing();
        var before = JObject.FromObject(state).ToString();

        var result = Reveal(state, Guest, "r1", 0, 0, 1);

        Assert.That(result.Outcome, Is.EqualTo(MatchOutcome.NotYourTurn));
        Assert.That(result.Changed, Is.False);
        Assert.That(JObject.FromObject(state).ToString(), Is.EqualTo(before));
    }

    [Test]
    public void UnaJugadaSobreUnEstadoViejoSeRechazaComoStale()
    {
        var state = Playing();
        Reveal(state, Host, "r1", 0, Find(state, mine: false), 1);

        // El Jugador 2 cree estar todavía en el turno 0.
        var result = Reveal(state, Guest, "r2", 0, Find(state, mine: false), 2);

        Assert.That(result.Outcome, Is.EqualTo(MatchOutcome.Stale));
    }

    [Test]
    public void UnIndiceFueraDelTableroSeRechazaEnLugarDeRomperElServidor()
    {
        var state = Playing();

        Assert.That(Reveal(state, Host, "r1", 0, -1, 1).Outcome, Is.EqualTo(MatchOutcome.InvalidCell));
        Assert.That(Reveal(state, Host, "r2", 0, 9999, 1).Outcome, Is.EqualTo(MatchOutcome.InvalidCell));
        Assert.That(EngineOf(state).TurnNumber, Is.EqualTo(0), "una jugada rechazada no consume turno");
        Assert.That(state.Moves, Is.Empty);
    }

    [Test]
    public void UnTipoDeJugadaDesconocidoSeRechaza()
    {
        var state = Playing();

        var result = MatchRules.ApplyPlay(state, Host, "r1", 0, "teletransporte", 0, At(1));

        Assert.That(result.Outcome, Is.EqualTo(MatchOutcome.InvalidMove));
        Assert.That(result.Changed, Is.False);
    }

    [Test]
    public void ElClienteNoPuedeFabricarseUnTimeoutNiUnAbandonoDelRival()
    {
        var state = Playing();

        Assert.That(MatchRules.ApplyPlay(state, Host, "r1", 0, MoveKind.Timeout, -1, At(1)).Outcome, Is.EqualTo(MatchOutcome.InvalidMove));
        Assert.That(MatchRules.ApplyPlay(state, Host, "r2", 0, MoveKind.Abandon, -1, At(1)).Outcome, Is.EqualTo(MatchOutcome.InvalidMove));
        Assert.That(state.Moves, Is.Empty);
    }

    [Test]
    public void NoSePuedeRevelarDosVecesLaMismaCasilla()
    {
        var state = Playing();
        var cell = Find(state, mine: false);
        Reveal(state, Host, "r1", 0, cell, 1);
        Reveal(state, Guest, "r2", 1, Find(state, mine: false), 2);

        var result = Reveal(state, Host, "r3", 2, cell, 3);

        Assert.That(result.Outcome, Is.EqualTo(MatchOutcome.AlreadyRevealed));
    }

    [Test]
    public void SinRivalNoSePuedeJugar()
    {
        var state = Waiting();

        Assert.That(Reveal(state, Host, "r1", 0, 0, 0).Outcome, Is.EqualTo(MatchOutcome.NotStarted));
        Assert.That(Advance(state, Host, "r2", 0, 0).Outcome, Is.EqualTo(MatchOutcome.NotStarted));
        Assert.That(Forfeit(state, Host, "r3", 0).Outcome, Is.EqualTo(MatchOutcome.NotStarted));
    }

    // ----------------------------------------------------------- idempotencia

    [Test]
    public void UnaPeticionAplicadaQuedaRegistradaParaDetectarReintentos()
    {
        var state = Playing();
        Reveal(state, Host, "mi-jugada", 0, Find(state, mine: false), 1);

        var processed = MatchRules.FindProcessed(state, "mi-jugada");

        Assert.That(processed, Is.Not.Null);
        Assert.That(processed!.ResultingTurnNumber, Is.EqualTo(1));
        Assert.That(MatchRules.FindProcessed(state, "otra"), Is.Null);
    }

    [Test]
    public void UnaJugadaRechazadaNoSeRegistraComoAplicada()
    {
        var state = Playing();
        Reveal(state, Guest, "fuera-de-turno", 0, 0, 1);

        Assert.That(MatchRules.FindProcessed(state, "fuera-de-turno"), Is.Null,
            "si se registrara, el reintento posterior (ya en su turno) se tomaría por repetido");
    }

    [Test]
    public void LasJugadasDelServidorNoSeConfundenConReintentos()
    {
        var state = Playing();
        MatchRules.ResolveTime(state, Host, At(MatchRules.TurnSeconds + MatchRules.GraceSeconds + 1));

        // El timeout lo registró el servidor, sin requestId: nadie puede "reintentarlo".
        Assert.That(state.Moves.Single().RequestId, Is.Empty);
        Assert.That(MatchRules.FindProcessed(state, ""), Is.Null);
    }

    // ----------------------------------------------------------------- niveles

    [Test]
    public void SoloElDuenoDelNivelPuedeAvanzarSiHayCasillasOcultas()
    {
        var state = Playing();
        Reveal(state, Host, "r1", 0, Find(state, mine: false), 1);

        // Ahora le toca al invitado, que NO es dueño del nivel 1.
        var result = Advance(state, Guest, "r2", 1, 2);

        Assert.That(result.Outcome, Is.EqualTo(MatchOutcome.NotLevelOwner));
    }

    [Test]
    public void AvanzarConCasillasOcultasCuestaUnaVidaYPasaElNivelAlOtroJugador()
    {
        var state = Playing();

        var result = Advance(state, Host, "r1", 0, 1);

        Assert.That(result.Outcome, Is.EqualTo(MatchOutcome.Applied));
        var view = ViewFor(state, Host);
        Assert.That(view.Level, Is.EqualTo(2));
        Assert.That(view.Lives1, Is.EqualTo(2));
        Assert.That(view.CurrentSeat, Is.EqualTo(2), "el nivel 2 es del Jugador 2");
        Assert.That(view.LastEvent!.Kind, Is.EqualTo(MoveKind.Advance));
    }

    // ------------------------------------------------------------- tiempo

    [Test]
    public void DentroDelMargenDeCortesiaNoSeAplicaNingunTimeout()
    {
        var state = Playing();

        var changed = MatchRules.ResolveTime(state, Guest, At(MatchRules.TurnSeconds + MatchRules.GraceSeconds - 1));

        Assert.That(changed, Is.False);
        Assert.That(state.Moves, Is.Empty);
    }

    [Test]
    public void AgotarElTiempoCuestaUnaVidaYPasaElTurno()
    {
        var state = Playing();

        var changed = MatchRules.ResolveTime(state, Guest, At(MatchRules.TurnSeconds + MatchRules.GraceSeconds + 1));

        Assert.That(changed, Is.True);
        var view = ViewFor(state, Guest);
        Assert.That(view.Lives1, Is.EqualTo(2));
        Assert.That(view.CurrentSeat, Is.EqualTo(2));
        Assert.That(view.LastEvent!.Kind, Is.EqualTo(MoveKind.Timeout));
        Assert.That(Deadline(view), Is.EqualTo(At(2 * MatchRules.TurnSeconds)),
            "el plazo del rival se cuenta desde el vencimiento anterior, no desde 'ahora'");
    }

    [Test]
    public void ElTimeoutQuedaRegistradoComoJugadaYElReplayLoReproduce()
    {
        var state = Playing();
        MatchRules.ResolveTime(state, Guest, At(MatchRules.TurnSeconds + MatchRules.GraceSeconds + 1));

        var move = state.Moves.Single();
        Assert.That(move.Kind, Is.EqualTo(MoveKind.Timeout));
        Assert.That(move.Seat, Is.EqualTo(1));
        Assert.That(EngineOf(state).PlayerOneLives, Is.EqualTo(2));
    }

    [Test]
    public void UnaJugadaTardiaSeRechazaPeroElTimeoutQuedaGuardado()
    {
        var state = Playing();

        // El anfitrión llega 30 s tarde con una jugada que estaba sobre el turno 0.
        var result = Reveal(state, Host, "tarde", 0, Find(state, mine: false), 30);

        Assert.That(result.Outcome, Is.EqualTo(MatchOutcome.Stale));
        Assert.That(result.Changed, Is.True, "hay que persistir el timeout aunque la jugada se rechace");
        Assert.That(result.GameChanged, Is.True);
        Assert.That(EngineOf(state).PlayerOneLives, Is.EqualTo(2));
    }

    // ------------------------------------------------- actividad y abandono

    [Test]
    public void ConsultarRegistraLaActividadPeroSoloSiLaMarcaEstaVieja()
    {
        // Una sala que espera rival: el anfitrión sondea y no hay turnos que puedan vencer.
        var state = Waiting();

        Assert.That(MatchRules.Observe(state, Host, At(5)).Changed, Is.False, "marca reciente: no se escribe");
        var late = MatchRules.Observe(state, Host, At(MatchRules.ActivityResolutionSeconds + 1));

        Assert.That(late.Changed, Is.True);
        Assert.That(late.GameChanged, Is.False, "solo cambió la actividad: no hay nada que avisar al rival");
        Assert.That(state.HostLastActivityAtUnixSeconds, Is.EqualTo(MatchRules.Unix(At(MatchRules.ActivityResolutionSeconds + 1))));
    }

    [Test]
    public void SiElRivalLlevaMuchoSinActividadGanaQuienConsulta()
    {
        var state = Playing();

        // El invitado se fue (su marca sigue en T0). El anfitrión sigue activo hasta ahora.
        MatchRules.Touch(state, Host, MatchRules.Unix(At(MatchRules.AbandonTimeoutSeconds)), force: true);
        MatchRules.Observe(state, Host, At(MatchRules.AbandonTimeoutSeconds + 1));

        Assert.That(state.Status, Is.EqualTo(MatchStatus.Finished));
        Assert.That(state.EndReason, Is.EqualTo("abandoned"));
        Assert.That(state.WinnerSeat, Is.EqualTo(1));
        Assert.That(state.Moves.Last().Kind, Is.EqualTo(MoveKind.Abandon));
        Assert.That(state.Moves.Last().Seat, Is.EqualTo(2));
    }

    [Test]
    public void SiSeFueronLosDosGanaElPrimeroQueVuelveYNoElQueDejoDeJugarAntes()
    {
        var state = Playing();

        // Pasa una hora sin que nadie llame. Vuelve el invitado.
        MatchRules.Observe(state, Guest, At(3600));

        Assert.That(state.Status, Is.EqualTo(MatchStatus.Finished));
        Assert.That(state.EndReason, Is.EqualTo("abandoned"));
        Assert.That(state.WinnerSeat, Is.EqualTo(2), "quien vuelve gana; el anfitrión no estaba");
    }

    [Test]
    public void UnJugadorQueSigueJugandoNoSeDeclaraAbandonado()
    {
        var state = Playing();

        // Los dos juegan con normalidad, una jugada cada 10 s. El nivel 1 (2x2) solo tiene 2 casillas
        // seguras, así que: J1 revela, J2 revela, J1 avanza (tablero limpio, sin penalización) y J2,
        // dueño del nivel 2, revela.
        Assert.That(Reveal(state, Host, "r0", 0, Find(state, mine: false), 10).Outcome, Is.EqualTo(MatchOutcome.Applied));
        Assert.That(Reveal(state, Guest, "r1", 1, Find(state, mine: false), 20).Outcome, Is.EqualTo(MatchOutcome.Applied));
        Assert.That(Advance(state, Host, "r2", 2, 30).Outcome, Is.EqualTo(MatchOutcome.Applied));
        Assert.That(Reveal(state, Guest, "r3", 3, Find(state, mine: false), 40).Outcome, Is.EqualTo(MatchOutcome.Applied));

        Assert.That(state.Status, Is.EqualTo(MatchStatus.Playing));
    }

    [Test]
    public void LaEsperaEnLaSalaNoCuentaComoInactividadDelAnfitrion()
    {
        var state = Waiting();

        // El rival entra 5 minutos después de que se creó la sala.
        var joinAt = At(300);
        MatchRules.StartWithGuest(state, Guest, Seed, joinAt);
        MatchRules.Observe(state, Guest, At(301));

        Assert.That(state.Status, Is.EqualTo(MatchStatus.Playing), "el anfitrión no se declara abandonado nada más empezar");
    }

    // --------------------------------------------------------------- abandono voluntario

    [Test]
    public void AbandonarDaLaVictoriaAlRivalEnCualquierMomento()
    {
        var state = Playing();

        // El invitado abandona aunque no sea su turno.
        var result = Forfeit(state, Guest, "r-ff", 1);

        Assert.That(result.Outcome, Is.EqualTo(MatchOutcome.Applied));
        Assert.That(state.Status, Is.EqualTo(MatchStatus.Finished));
        Assert.That(state.WinnerSeat, Is.EqualTo(1));
        Assert.That(state.EndReason, Is.EqualTo("forfeit"));
        Assert.That(state.Moves.Last().Kind, Is.EqualTo(MoveKind.Forfeit));
    }

    [Test]
    public void UnaPartidaTerminadaNoAceptaMasJugadas()
    {
        var state = Playing();
        Forfeit(state, Guest, "r-ff", 1);

        Assert.That(Reveal(state, Host, "r1", 0, 0, 2).Outcome, Is.EqualTo(MatchOutcome.MatchFinished));
        Assert.That(Forfeit(state, Host, "r2", 2).Outcome, Is.EqualTo(MatchOutcome.MatchFinished));
    }

    // ----------------------------------------------------------- timeouts encadenados

    [Test]
    public void SiSePasaDeLargoDeTodosLosTurnosLaPartidaTerminaPorVidas()
    {
        var state = Playing();

        // Sin que nadie consulte durante un rato, pero con los dos "presentes" (marcas al día),
        // se encadenan los timeouts de ambos hasta que alguien se queda sin vidas.
        MatchRules.Touch(state, Host, MatchRules.Unix(At(130)), force: true);
        MatchRules.Touch(state, Guest, MatchRules.Unix(At(130)), force: true);
        MatchRules.ResolveTime(state, Host, At(130));

        Assert.That(state.Status, Is.EqualTo(MatchStatus.Finished));
        // Turnos virtuales: J1, J2, J1, J2, J1 -> J1 llega a 0 primero.
        Assert.That(state.WinnerSeat, Is.EqualTo(2));
        Assert.That(state.EndReason, Is.EqualTo("lives"));
    }

    // ------------------------------------------------------------ código/sala

    [Test]
    public void ElCodigoSeDerivaDelRequestIdYEsLegible()
    {
        var a = MatchCode.FromRequestId("abc");
        var b = MatchCode.FromRequestId("abc");

        Assert.That(a, Is.EqualTo(b), "reintentar 'crear sala' cae en el mismo código");
        Assert.That(MatchCode.IsWellFormed(a), Is.True);
        Assert.That(a, Does.Not.Contain("0").And.Not.Contain("1").And.Not.Contain("O").And.Not.Contain("I"));
        Assert.That(MatchCode.FromRequestId("abc", 1), Is.Not.EqualTo(a), "otro intento, otro candidato");
    }

    [Test]
    public void ElCodigoTecleadoSeNormaliza()
    {
        Assert.That(MatchCode.Normalize("  abcd "), Is.EqualTo("ABCD"));
        Assert.That(MatchCode.IsWellFormed("ab1d"), Is.False);
        Assert.That(MatchCode.IsWellFormed("ABC"), Is.False);
    }
}
