# Módulo WipedOut — buscaminas por turnos con servidor autoritativo

Partida online de dos jugadores. **El servidor (Cloud Code) es la única autoridad**: ahí corre el
motor de reglas y ahí viven las minas. Los clientes solo envían intenciones y pintan lo que el
servidor les cuenta. El aviso al rival va por **push de Cloud Code (Wire)**.

## Por qué existe el módulo (la idea en 3 líneas)

1. En un buscaminas, **dónde están las minas es información oculta**. Si el cliente la conoce (o conoce
   la semilla que las genera), puede hacer trampa.
2. Por eso el estado vive en Cloud Save como *custom data privado* y solo Cloud Code lo lee y escribe.
3. A cada jugador solo se le envía una **vista** (`MatchView`): celdas ocultas (`-1`), minas ya
   pisadas (`-2`) o el número de vecinas (`0-8`). Las minas completas solo se envían al terminar.

## Estructura

```
CloudCode.Modules/WipedOut/
├── Project/        módulo de Cloud Code (net6.0)
│   ├── WipedOutModule.cs   funciones expuestas + push liviano al rival
│   ├── MatchRules.cs       reglas de la sala, SIN dependencias de Cloud Code (se prueban sin servidor)
│   ├── MatchRepository.cs  Cloud Save custom data privado + write lock
│   ├── MatchState.cs       lo que se guarda: semilla secreta + lista de jugadas
│   ├── MatchView.cs        lo único que ve un cliente
│   ├── MatchJson.cs        cómo viaja la vista (JSON camelCase)
│   ├── MatchCode.cs        código de sala derivado del requestId
│   └── GameProtocol.cs     versión del protocolo (gemela de la del cliente)
└── TestProject/    pruebas NUnit
```

El motor de reglas **no está duplicado**: `Project/WipedOut.csproj` compila directamente
`Assets/Scripts/Gameplay/*.cs`, los mismos archivos que usa el juego local en Unity.

## Mensajes (cliente → servidor)

Todas las funciones devuelven el JSON de una `MatchView`. El servidor identifica al jugador por
`context.PlayerId` (el token), **nunca por un campo que mande el cliente**. `matchId` es el código de sala.

| Función | Parámetros | Qué hace |
|---|---|---|
| `CreateMatch` | `requestId` | Crea la sala (jugador 1) y devuelve su código. Idempotente: el código se deriva del `requestId`. |
| `JoinMatch` | `matchId`, `requestId` | Entra el jugador 2; el servidor sortea la **semilla secreta** y avisa al anfitrión. |
| `PlayMove` | `matchId`, `requestId`, `expectedTurnNumber`, `kind`, `cell` | Valida y aplica una jugada. `kind` = `reveal` (con `cell`), `advance` o `forfeit`. |
| `GetMatchState` | `matchId` | Devuelve la vista. Registra la actividad de quien llama y resuelve plazos vencidos y abandono. |

`outcome` de la respuesta: `applied`, `replayed`, `not_started`, `match_finished`, `stale`,
`not_your_turn`, `not_level_owner`, `already_revealed`, `invalid_cell`, `invalid_move`, `conflict`.
Los rechazos viajan ahí, no como excepción: no son errores, son parte normal del juego.

El servidor también registra por su cuenta las jugadas `timeout` y `abandon`; el cliente **no puede pedirlas**.

## Mensajes (servidor → cliente): push de Cloud Code

Al terminar una jugada que cambió la partida, el servidor envía **al rival**:

```
IPushClient.SendPlayerMessageAsync(context, "{ \"type\": \"match_updated\", \"matchId\": \"ABCD\" }", "match_updated", rivalPlayerId)
```

- **El payload es liviano y nunca lleva el estado.** El cliente no se fía de su contenido: lo usa como
  señal y vuelve a pedir `GetMatchState`.
- El cliente se suscribe con `CloudCodeService.Instance.SubscribeToPlayerMessagesAsync` y filtra por `MessageType`.
- **Sondeo de respaldo** (~10 s) por si se pierde un push.
- Si el push falla, la jugada **no** se estropea: ya quedó guardada.
- Solo funciona con la app abierta.

## Estado: semilla secreta + lista de jugadas (replay)

No se guarda una foto del tablero. `MatchState` guarda la **semilla** (secreta) y `Moves`, una lista
**solo de agregar** (`reveal`, `advance`, `timeout`, `forfeit`, `abandon`, con asiento, casilla y hora). En cada
llamada `MatchRules.Replay` la reproduce con un motor nuevo y se obtiene, de forma determinista, el tablero,
el turno y las vidas. Esto exige que el motor sea determinista: cada nivel sale solo de (semilla, nivel).

## Orden de validación en `PlayMove`

1. ¿Ya procesé este `requestId`? → `replayed` (**antes** que nada: si no, un reintento legítimo se
   rechazaría con "no es tu turno" porque la primera petición sí llegó).
2. ¿Participas en la partida? Si no, error.
3. ¿Empezó y no terminó? → `not_started` / `match_finished`.
4. Se registra tu actividad y se resuelve lo vencido (abandono del rival y plazos de turno).
5. ¿`expectedTurnNumber` coincide? → `stale`.
6. ¿Es tu turno? → `not_your_turn`. (`forfeit` se permite siempre.)
7. Reglas del juego con el motor: casilla válida y sin revelar, dueño del nivel...
8. Se guarda con **write lock**; si otra escritura ganó la carrera → `conflict` (con el estado real).
9. Se avisa al rival por push.

## Tiempo por turno, actividad y abandono (reloj del servidor)

Cloud Code solo ejecuta cuando alguien lo llama: **no hay temporizadores**. Todo se resuelve de forma
**reactiva**: cualquier `GetMatchState` o `PlayMove` calcula primero qué habría pasado si el reloj hubiera corrido.

- **Plazo de turno:** 20 s (`MatchRules.TurnSeconds`) + 3 s de margen por la latencia. El diseño original
  baja a 2 s; con push y sondeo de ~10 s eso es injugable. Se mide con `TurnStartedAt` (reloj del servidor).
  Agotar el plazo cuesta una vida y pasa el turno (decisión de diseño: se cambia en `MatchEngine.ExpireTurn`).
  Cada plazo se cuenta desde el anterior, no desde "ahora".
- **Abandono por inactividad:** cada llamada registra la actividad del jugador (`LastActivityAt`, con un
  margen de 30 s para no escribir en cada sondeo). Si el rival lleva más de 180 s
  (`MatchRules.AbandonTimeoutSeconds`) sin actividad, **quien consulta gana**. Se comprueba **antes** que los
  plazos: si los dos se fueron, gana el primero que vuelve (si no, perdería quien primero dejó de jugar, aunque
  sea quien vuelve).
- **Abandono voluntario:** `PlayMove` con `kind = forfeit`, en cualquier momento.
- **Reconexión:** el jugador vuelve a la sala con su código y `GetMatchState`.

## Versión del protocolo

`GameProtocol.Version` existe en el servidor (`Project/GameProtocol.cs`) y en el cliente
(`Assets/Scripts/Online/GameProtocol.cs`). Cada vista lleva la versión del servidor; si no coincide con la del
cliente, este avisa de que hay que actualizar en lugar de interpretar mal la respuesta. **Hay que subir las dos
a la vez cuando cambie el contrato de un RPC** (una prueba comprueba que coinciden).

## Probar

```bash
dotnet test CloudCode.Modules/WipedOut/WipedOut.sln
```

Corren sin servidor ni Unity: las pruebas originales del motor, las del replay y las de `MatchRules`
(turnos, idempotencia, plazos, abandono, **que la vista no contenga la semilla ni las minas ocultas**) y las de
contrato con el cliente (versión del protocolo y que el JSON del servidor llene los campos del cliente).

## Desplegar

1. En Unity, con el paquete Deployment, abrir *Deployment* y desplegar `Assets/CloudCode/WipedOut.ccmr`
   (empaqueta `WipedOut.sln`). También con el UGS CLI: `ugs deploy Assets/CloudCode/WipedOut.ccmr`.
2. El módulo usa Cloud Save (`AddGameApiClient`) y mensajes push (`IPushClient`, ya registrado por
   defecto con Core 0.0.5 y Apis 0.0.26).

## Qué NO está probado ni cubierto

- **No se ha ejecutado contra un proyecto UGS real.** Lo verificado: el módulo compila contra las librerías de
  Cloud Code, las reglas pasan sus pruebas y el contrato con los modelos del cliente coincide. **El cliente de
  Unity compila** (editor 6000.3.23f1, sobre una copia mínima con solo Core, Authentication, CloudSave, CloudCode
  y uGUI) y pasa las 42 pruebas EditMode del motor, pero **la pantalla online (`OnlineMatchScreen`) nunca se ha
  visto funcionando**: no hay prueba de que el botón ONLINE, el lobby y el tablero se dibujen como se espera.
  Falta probar el flujo con dos clientes.
- **Matchmaker:** el emparejamiento es por código de sala (como `TurnMatch`), no por ticket + `handoffId`.
- **`SweepAbandonedMatches`** (barrido programado de partidas donde se fueron los dos) y limpieza de salas
  terminadas: no existen. Solo hay abandono reactivo.
- **Rating, estadísticas y premios:** siguen escritos por el cliente en clase *Default* de Cloud Save. Deben
  calcularse en el servidor y guardarse en Protected.
- Tiempos y umbrales (`TurnSeconds`, `AbandonTimeoutSeconds`) son constantes; en TTTXO viven en Remote Config.
- Política de acceso de Cloud Code para restringir quién llama a las funciones.
- Creación simultánea del mismo código (mismo límite que `TurnMatch`).
- El módulo apunta a `net6.0` (como `TurnMatch`); TTTXO usa .NET 9.
