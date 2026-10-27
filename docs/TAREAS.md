# Tareas para seguir desarrollando — partida online de Wiped-Out

Este PR deja una partida online **funcionando en el papel y en las pruebas automáticas**, pero hay mucho por hacer.
Las tareas están repartidas entre los tres. Cada una explica **por qué** existe, **dónde** tocar, **qué hacer** y
**cómo saber que terminó**. No dan el código hecho a propósito: la idea es que lo entiendan, no que lo copien.

> **Antes de nada (los tres):** lean `CloudCode.Modules/WipedOut/README.md` y asegúrense de poder contestar, sin mirar
> el código:
> 1. ¿Por qué la semilla de las minas **no puede** llegar nunca al cliente, aunque no se envíe el array de minas?
> 2. ¿Por qué `PlayMove` comprueba el `requestId` **antes** que el turno?
> 3. ¿Por qué el aviso push no lleva el estado y qué pasa si se pierde?
> 4. ¿Por qué el abandono se comprueba **antes** que los plazos de turno?

## Cómo trabajamos

- **Cada uno en su propia rama** (`steve/...`, `erwin/...`, `salim/...`) y **un PR por tarea** hacia `Avance`.
  Nadie hace push directo a `Avance`, y nunca `--force`.
- **Commits pequeños y descriptivos** (qué cambia y por qué), no "Avance 3".
- Antes de abrir el PR: `dotnet test CloudCode.Modules/WipedOut/WipedOut.sln` en verde, y si tocaron Unity, las
  pruebas EditMode también.
- **Quien revisa no es quien escribe:** cada PR lo revisa otra persona del grupo.
- Todo lo que descubran probando va como respuesta en el PR, con el formato de `docs/PRUEBAS.md`.

## Decisión de grupo (hagan esto primero, en papel)

Creen `docs/DECISIONES.md` entre los tres y respondan con una frase de justificación cada una:

1. **Emparejamiento:** ¿seguimos con código de sala o usamos Matchmaker de Unity? ¿Qué ganamos y qué perdemos?
2. **Plazo vencido:** ahora cuesta **una vida** y pasa el turno. ¿Debería costar la partida? ¿Por qué?
3. **Duración del turno:** el diseño original era de 2 a 5 s por turno. Online usamos **20 s**. ¿Por qué 2 s no funciona
   con aviso push y sondeo de ~10 s?
4. **Estado:** guardamos **semilla + lista de jugadas** y la reproducimos. ¿Qué ventaja tiene sobre guardar una foto
   del tablero?

---

## Salim — rating y estadísticas en el servidor

### S1. Mover el rating y las estadísticas a una clase de acceso protegida

- **Por qué:** hoy `wipedout_save` (con `rating`, victorias, etc.) se guarda en Cloud Save con las opciones por
  defecto, es decir, clase **Default**. Esa clase **la puede escribir el propio jugador** con su token, sin pasar por
  el juego. Cualquiera podría ponerse 5000 de rating. Lo que decide el servidor debe ir en **Protected** (el jugador lo
  lee, pero no lo escribe).
- **Dónde:** `Assets/Scripts/CloudSave/` (`CloudSaveStore`, `PlayerSaveData`) y `CloudCode.Modules/WipedOut/Project/`.
- **Qué hacer:**
  1. Dibujen primero qué datos son **del servidor** (rating, victorias, derrotas) y cuáles **del jugador** (nombre,
     avatar).
  2. Cuando la partida pasa a `finished`, el **servidor** calcula el resultado y lo guarda para cada jugador en datos
     de jugador con clase de acceso **Protected**.
  3. Debe liquidarse **una sola vez**: guarden una marca de "ya liquidado" en `MatchState` para que un reintento no
     sume dos veces.
  4. En el cliente, el rating pasa a ser de **solo lectura**: se lee, no se escribe.
- **Cómo saber que terminó:** una prueba unitaria del cálculo, una prueba de que liquidar dos veces no suma dos veces,
  y una prueba manual: intenten desde el cliente guardar un rating falso y vean que Cloud Save **lo rechaza**.
- **Para pensar:** ¿qué datos deberían ser **Private** (el jugador ni siquiera los ve)?

### S2. Fusionar `Avance` en `main`

- **Por qué:** `main` tiene solo el commit inicial y su trabajo vive en `Avance`. Quien abra el repo en GitHub ve un
  proyecto vacío.
- **Qué hacer:** coordinen con el grupo y abran **un PR de `Avance` hacia `main`** cuando este PR esté fusionado y las
  pruebas pasen. Sin `--force`.

---

## Steve — pruebas, reconexión y limpieza del proyecto

### V1. Ejecutar las pruebas de `docs/PRUEBAS.md` y reportar

- **Por qué:** es la tarea más valiosa ahora mismo: el código **nunca se ha ejecutado contra Unity Gaming Services**.
- **Qué hacer:** sigan T1 a T15 en orden. Respondan en el PR con el formato de reporte. Si encuentran un fallo, **descríbanlo
  antes de arreglarlo** (qué pasó, cómo repetirlo, qué esperaban).
- **Cómo saber que terminó:** todas las pruebas tienen un resultado (OK / FALLA / A MEDIAS) con evidencia.

### V2. Que el cliente recuerde la partida (reconexión)

- **Por qué:** si el jugador cierra la app a mitad de partida, hoy tiene que **recordar y escribir el código** otra vez.
  Es el criterio de robustez de "reconexión".
- **Dónde:** `Assets/Scripts/Online/OnlineMatchService.cs` y `OnlineMatchScreen.cs`.
- **Qué hacer:** guarden en local (por ejemplo `PlayerPrefs`) el código de la partida en curso, y ofrezcan un botón
  **CONTINUE** en el lobby que pida el estado al servidor. Cuando la partida termina, olviden el código.
- **Cuidado:** el servidor es la verdad. El código guardado en local solo sirve para saber **qué pedir**; si el servidor
  dice que la partida ya terminó o que no existe, hay que limpiar el código guardado.
- **Cómo saber que terminó:** prueba T12 sin escribir el código.

### V3. Dos arreglos pequeños del proyecto

- **Dependencia local:** `Packages/manifest.json` carga `com.coplaydev.unity-mcp` desde `C:/Users/USER/...`, una ruta que
  solo existe en un equipo. Quítenla del manifiesto y documenten en el README cómo instalarla quien la use.
- **Estadísticas del juego local:** en hot-seat, `RecordGameLost()` suma la derrota a la cuenta conectada gane quien gane,
  y se cuentan las minas de **los dos** jugadores. Decidan qué debe contarse en una partida local y corríjanlo.

---

## Erwin — pruebas del servidor, documentación y limpieza de partidas

### E1. Mover la reconexión a `MatchRules` y probarla

- **Por qué:** en `WipedOutModule.JoinMatch`, el caso "ya participas en esta partida, así que te devuelvo el estado"
  está **escrito dentro del módulo**, donde no hay pruebas. La regla es tan simple que cabe en `MatchRules` (que **no**
  depende de Cloud Code y se prueba sin servidor). Es una buena primera tarea para entender cómo está dividido el código.
- **Dónde:** `CloudCode.Modules/WipedOut/Project/MatchRules.cs` y `.../TestProject/MatchRulesTests.cs`.
- **Qué hacer:** creen en `MatchRules` una función pura que diga si un jugador puede entrar, reingresar o debe ser
  rechazado, usen esa función desde el módulo, y escriban pruebas para: anfitrión que reingresa, invitado que reingresa,
  tercer jugador que intenta entrar a una sala llena, y sala sin invitado.
- **Cómo saber que terminó:** `dotnet test` en verde con las pruebas nuevas.

### E2. README del proyecto y `CLAUDE.md` del repo

- **Por qué:** hoy no hay ningún README: quien llega al repo no sabe qué es, ni cómo ejecutarlo. Tampoco hay `CLAUDE.md`
  para que Claude Code trabaje con las mismas reglas del equipo.
- **Qué hacer:** un `README.md` en la raíz (qué es el juego, reglas, cómo abrirlo en Unity, cómo correr las pruebas, cómo
  configurar UGS y desplegar el módulo) y un `CLAUDE.md` con las convenciones: reglas puras en `Assets/Scripts/Gameplay`,
  **el cliente nunca decide nada**, la semilla nunca sale del servidor, lo que decide el servidor va en Protected/Private.
- **Cómo saber que terminó:** alguien que **no** conoce el proyecto lo abre y lo ejecuta siguiendo solo el README.

### E3. Barrido de partidas abandonadas y limpieza

- **Por qué:** el abandono se resuelve cuando alguien consulta la partida. Si se van **los dos para siempre**, nadie
  consulta y la partida queda guardada eternamente (y ocupa espacio).
- **Qué hacer:** **primero en papel** (en `docs/DECISIONES.md`): ¿cómo sabe el barrido qué partidas siguen activas? (hace
  falta un índice) ¿cuándo se considera muerta una partida? ¿qué se borra y qué se conserva? Después implementen una
  función de Cloud Code que revise las partidas activas, disparada por un **trigger programado** de Cloud Code
  (consulten su documentación).
- **Cómo saber que terminó:** una prueba de la regla ("¿esta partida está muerta?") sin servidor, y una prueba manual
  dejando una sala abandonada.

---

## Para todos, más adelante

- **Configuración fuera del código:** `TurnSeconds` (20 s) y `AbandonTimeoutSeconds` (180 s) son constantes. Si la
  duración del turno estuviera en **Remote Config**, se podría cambiar sin volver a desplegar. Pregunta de diseño: ¿quién
  lee Remote Config, el cliente o solo el servidor? (Pista: si lo leen los dos por separado, pueden discrepar.)
- **Matchmaker:** cuando tomen la decisión de `docs/DECISIONES.md`, si eligen Matchmaker el flujo `CreateMatch` cambia
  bastante (los dos clientes la llaman con un identificador común y debe ser idempotente). Diséñenlo en papel antes.
- **Versión del protocolo:** cada vez que cambien un parámetro de un RPC o la forma de la vista, suban
  `GameProtocol.Version` en el servidor **y** en el cliente a la vez. Hay una prueba que comprueba que coinciden.
