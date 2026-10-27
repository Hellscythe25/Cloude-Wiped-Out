using System.Reflection;
using CloudGame.Gameplay;
using CloudGame.Online;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WipedOut;

namespace WipedOut.Tests;

/// <summary>
/// El contrato entre el servidor y el cliente de Unity. No prueban el cliente (eso necesita Unity),
/// pero sí lo que más se rompe en silencio: que ambos hablen la misma versión del protocolo y que el
/// JSON del servidor encaje con la clase que Unity lee con <c>JsonUtility</c>.
/// </summary>
public class ClientContractTests
{
    private const string Host = "player-host";
    private const string Guest = "player-guest";
    private static readonly DateTime T0 = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private static MatchState Playing()
    {
        var state = MatchRules.NewMatch("ABCD", Host, "req", T0);
        MatchRules.StartWithGuest(state, Guest, 4321, T0);
        return state;
    }

    private static OnlineMatchView ClientView(MatchState state, string viewer)
    {
        var json = MatchJson.Serialize(MatchRules.BuildView(state, viewer, MatchOutcome.Applied, "", T0));
        // JsonUtility (Unity) deja las fechas como texto; Newtonsoft por defecto las convertiría.
        var settings = new JsonSerializerSettings { DateParseHandling = DateParseHandling.None };
        return JsonConvert.DeserializeObject<OnlineMatchView>(json, settings)!;
    }

    [Test]
    public void ElClienteYElServidorHablanLaMismaVersionDelProtocolo()
    {
        Assert.That(CloudGame.Online.GameProtocol.Version, Is.EqualTo(WipedOut.GameProtocol.Version),
            "hay que subir las dos constantes a la vez cuando cambia un RPC");
    }

    [Test]
    public void LaVistaDelServidorSeLeeEnElModeloDelCliente()
    {
        var view = ClientView(Playing(), Host);

        Assert.That(view.protocolVersion, Is.EqualTo(CloudGame.Online.GameProtocol.Version));
        Assert.That(view.matchCode, Is.EqualTo("ABCD"));
        Assert.That(view.status, Is.EqualTo(OnlineStatus.Playing));
        Assert.That(view.mySeat, Is.EqualTo(1));
        Assert.That(view.size * view.size, Is.EqualTo(view.cells.Length));
        Assert.That(view.IsMyTurn, Is.True);
        Assert.That(ClientView(Playing(), Guest).IsMyTurn, Is.False);
        Assert.That(view.SecondsLeftAtServerTime(), Is.EqualTo(MatchRules.TurnSeconds).Within(0.5));
    }

    [Test]
    public void TodosLosCamposDelClienteExistenEnElJsonDelServidor()
    {
        // Una vista de partida terminada trae todos los campos (incluidas las minas y el último evento).
        var state = Playing();
        MatchRules.ApplyPlay(state, Guest, "ff", 0, MoveKind.Forfeit, -1, T0.AddSeconds(1));
        var json = JObject.Parse(MatchJson.Serialize(MatchRules.BuildView(state, Host, MatchOutcome.Applied, "", T0)));

        var missing = typeof(OnlineMatchView)
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Select(f => f.Name)
            .Where(name => json.Property(name) == null)
            .ToList();

        Assert.That(missing, Is.Empty,
            "JsonUtility lee por nombre exacto: un campo que cambie en el servidor y no en el cliente quedaría vacío en silencio");
    }

    [Test]
    public void ElTableroPublicoDelClienteNoConoceLasMinasOcultas()
    {
        var state = Playing();
        var engine = MatchRules.Replay(state).Engine!;
        var mine = Enumerable.Range(0, engine.Board.CellCount).First(engine.Board.IsMine);

        MatchRules.ApplyPlay(state, Host, "r1", 0, MoveKind.Reveal, mine, T0.AddSeconds(1));
        var view = ClientView(state, Host);
        var board = new PublicBoard(view);

        Assert.That(board.IsRevealed(mine) && board.IsMine(mine), Is.True, "la pisada sí se ve");
        for (var i = 0; i < board.CellCount; i++)
        {
            if (!board.IsRevealed(i))
            {
                Assert.That(board.IsMine(i), Is.False, $"la celda oculta {i} no debe delatar si es mina");
            }
        }
    }

    [Test]
    public void AlTerminarElTableroDelClienteMuestraTodasLasMinas()
    {
        var state = Playing();
        MatchRules.ApplyPlay(state, Guest, "ff", 0, MoveKind.Forfeit, -1, T0.AddSeconds(1));
        var truth = MatchRules.Replay(state).Engine!.Board;

        var board = new PublicBoard(ClientView(state, Host));

        for (var i = 0; i < truth.CellCount; i++)
        {
            Assert.That(board.IsMine(i), Is.EqualTo(truth.IsMine(i)), $"celda {i}");
        }
    }

    [Test]
    public void ElAvisoPushEsLivianoYNoLlevaEstado()
    {
        // La forma exacta del payload que envía el módulo (ver WipedOutModule.NotifyOpponentAsync).
        var payload = JsonConvert.SerializeObject(new { type = WipedOutModule.PushMessageType, matchId = "ABCD" });
        var keys = JObject.Parse(payload).Properties().Select(p => p.Name).ToList();

        Assert.That(WipedOutModule.PushMessageType, Is.EqualTo("match_updated"));
        Assert.That(keys, Is.EquivalentTo(new[] { "type", "matchId" }));
    }
}
