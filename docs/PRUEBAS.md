# Actividades de testing — partida online de Wiped-Out

Este documento es para que **ustedes tres** prueben lo que trae el PR y nos cuenten qué pasó. Aquí hay código
que **compila y pasa pruebas automáticas**, pero que **nunca se ha ejecutado contra Unity Gaming Services (UGS)
ni se ha visto funcionando en pantalla**. Lo que ustedes encuentren es justo la retroalimentación que se necesita.

## Cómo reportar

Para cada prueba, respondan en el PR (o abran un *issue*) con este formato mínimo:

```
Prueba: T5
Resultado: OK / FALLA / A MEDIAS
Qué pasó: (una o dos frases)
Evidencia: captura de pantalla y/o texto de la consola de Unity (Window > General > Console)
Datos: código de sala, hora aproximada, qué cliente (editor o build)
```

Si algo **falla**, no lo arreglen primero en silencio: reporten. Un fallo bien descrito es más valioso que un parche
que no entienden. Si quieren corregirlo ustedes, háganlo en una rama propia y abran su PR (ver `docs/TAREAS.md`).

## Requisitos previos

- Unity **6000.3.22f1** (la versión de `ProjectSettings/ProjectVersion.txt`).
- El proyecto vinculado a un proyecto UGS (ya trae `cloudProjectId`) con **Authentication, Cloud Save y Cloud Code**
  habilitados en el dashboard de Unity. Usen un entorno de desarrollo, no uno de producción.
- `dotnet` SDK instalado (para la prueba T1).
- Para quien no tenga el paquete `com.coplaydev.unity-mcp`: `Packages/manifest.json` lo carga desde una ruta
  local (`C:/Users/USER/...`). Si Unity se queja, quítenlo de su copia local (no lo suban). Es una de las tareas.

---

## Parte A — Sin servidor (rápidas, hagan estas primero)

### T1. Pruebas automáticas del módulo y del motor (`dotnet`)

- **Pasos:** en una terminal, desde la raíz del repo: `dotnet test CloudCode.Modules/WipedOut/WipedOut.sln`
- **Esperado:** `Correctas ... Superado: 88, Con error: 0`.
- **Si falla, reporten:** el nombre de la prueba que falla y el mensaje completo.

### T2. Pruebas EditMode en Unity

- **Pasos:** abran el proyecto > `Window > General > Test Runner` > pestaña **EditMode** > **Run All**.
- **Esperado:** **42 pruebas en verde** (las 32 que ya tenían y 10 nuevas del motor).
- **Si falla, reporten:** nombre de la prueba y el error. Si hay errores de **compilación** en la Console, copien el primero.

### T3. La pantalla online se ve y no rompe el juego local

- **Pasos:** abran `SampleScene`, pulsen Play, inicien sesión (invitado sirve), y pasen a `GameScene`.
- **Esperado:**
  1. Aparece un botón **ONLINE** arriba a la derecha, sin tapar el marcador.
  2. El juego local (hot-seat) sigue funcionando igual que antes.
  3. Al pulsar ONLINE se ve el lobby: título, **CREATE ROOM**, un campo **CODE**, **JOIN** y **BACK**.
  4. **BACK** cierra el lobby y vuelve al juego local.
- **Si falla, reporten:** captura del problema (botón mal colocado, texto cortado, lobby que no abre) y la Console.
  *Ojo:* esta pantalla se construye por código (`Assets/Scripts/Online/OnlineMatchScreen.cs`) y es lo menos probado.

---

## Parte B — Contra UGS (necesita servidor desplegado)

### T4. Desplegar el módulo de Cloud Code

- **Pasos:** `Window > Deployment`, marcar `Assets/CloudCode/WipedOut.ccmr`, **Deploy**. (Alternativa con el UGS CLI:
  `ugs deploy Assets/CloudCode/WipedOut.ccmr`.)
- **Esperado:** el módulo **WipedOut** aparece desplegado con las funciones `CreateMatch`, `JoinMatch`, `PlayMove`
  y `GetMatchState`.
- **Si falla, reporten:** el mensaje de error del despliegue (suele ser de `dotnet`, NuGet o de permisos).

### Cómo montar dos clientes (T5 en adelante)

Necesitan **dos jugadores distintos** en la misma partida. Opciones: el Editor + una *build* de Windows, o dos *builds*.
Cada cliente debe iniciar sesión con una cuenta **diferente** (por ejemplo, "invitado" en cada uno: cada instalación es
otro jugador). Llamaremos **A** al que crea la sala y **B** al que se une.

### T5. Crear sala y unirse

- **Pasos:** A pulsa ONLINE > CREATE ROOM y anota el código de 4 letras. B pulsa ONLINE, escribe el código y JOIN.
- **Esperado:** A ve "WAITING FOR YOUR RIVAL" con el código; al unirse B, **A pasa a ver el tablero sin pulsar nada**
  (le llegó el aviso) y le dice que le toca (YOUR TURN). B ve el tablero y "RIVAL'S TURN".
- **Si falla, reporten:** quién no avanzó, cuánto esperaron, y los errores de Console de ambos.

### T6. Una jugada y el aviso al rival (push)

- **Pasos:** A revela una casilla.
- **Esperado:** B ve la casilla revelada **casi al instante** (aviso push de Cloud Code), y el turno pasa a B. Si el
  aviso no llega, B debe verla igualmente en **menos de ~10 s** (sondeo de respaldo).
- **Reporten:** cuánto tardó en aparecer en B (aprox.) y si fue por aviso o por sondeo.

### T7. Jugar fuera de turno

- **Pasos:** cuando le toca a B, A pulsa una casilla.
- **Esperado:** no pasa nada en el tablero y A lee "No es tu turno." (o similar). El turno no cambia.
- **Reporten:** si el servidor aceptó la jugada (sería un fallo grave de autoridad).

### T8. Reintento por pérdida de red

- **Pasos:** A desconecta la red (modo avión / desactivar el adaptador) y pulsa una casilla; reconecta la red y
  vuelve a pulsar **la misma casilla**.
- **Esperado:** la jugada cuenta **una sola vez**. No se pasan dos turnos ni se pierde la jugada.
- **Por qué importa:** la red pierde respuestas, no peticiones. El cliente reutiliza el mismo `requestId` en el reintento
  y el servidor lo reconoce (`replayed`).
- **Reporten:** si el turno avanzó dos veces o si la casilla quedó sin revelar.

### T9. Plazo de turno vencido

- **Pasos:** cuando le toca a A, **no hagan nada** durante ~25 s.
- **Esperado:** el contador baja a 0; después, A pierde **una vida** y el turno pasa a B (lo resuelve el servidor, no
  el cliente). Un jugador ve el cambio al consultar el servidor, así que puede tardar unos segundos.
- **Reporten:** a los cuántos segundos se resolvió, y si el contador de A y el de B coincidían.

### T10. Abandonar

- **Pasos:** A pulsa FORFEIT (en cualquier momento, aunque no sea su turno).
- **Esperado:** la partida termina; A ve "YOU LOSE", B ve "YOU WIN!" y **todas las minas quedan visibles** en ambos.
- **Reporten:** si alguno de los dos no vio el final, y si las minas se mostraron.

### T11. Un jugador desaparece (y, aparte, desaparecen los dos)

Son dos casos distintos; prueben los dos.

**T11a — se va solo uno (dura ~1-2 minutos).**
- **Pasos:** en plena partida cierren el cliente **B** y dejen **A abierto sin tocar nada** (el sondeo de A consulta al servidor).
- **Esperado:** los turnos de B se van venciendo (T9): B pierde una vida cada ~20 s y, al quedarse sin vidas, **A gana**
  ("Sin vidas."). Lo importante es que la partida **termine sola con A como ganador** y no se quede colgada.
- **Reporten:** cuánto tardó en terminar y el mensaje que vio A.

**T11b — se van los dos (dura ~4 minutos).**
- **Pasos:** en plena partida cierren **los dos** clientes y esperen **más de 3 minutos**. Después abran **solo A**, inicien
  sesión como el mismo jugador, pulsen ONLINE y en JOIN escriban el mismo código (ver T12).
- **Esperado:** A **gana por abandono del rival**: "Tu rival se desconectó." Gana el **primero que vuelve**, no quien
  dejó de jugar antes (es una decisión de diseño del servidor: el abandono se comprueba antes que los plazos).
- **Reporten:** el mensaje y si A ganó o perdió.

### T12. Reconexión

- **Pasos:** en plena partida, A cierra la app y la vuelve a abrir (menos de 3 minutos). Inicia sesión como el **mismo
  jugador** (misma cuenta o mismo dispositivo si es invitado), pulsa ONLINE y en JOIN escribe el **mismo código**.
- **Esperado:** A **vuelve a su partida** y ve el tablero como estaba. (El cliente no guarda el código: hay que
  escribirlo de nuevo. Es una mejora pendiente.)
- **Reporten:** si da error, y cuál.

### T13. La información oculta no sale del servidor (la más importante)

- **Pasos:** añadan temporalmente en `OnlineMatchService.CallAsync`, justo después de recibir `json`, esta línea:
  `Debug.Log(json);`. Jueguen unas jugadas y miren la Console. **No suban ese `Debug.Log`.**
- **Esperado:** mientras se juega, el texto de la respuesta **no contiene** las palabras `seed`, `moves` ni `mines`; el
  campo `cells` tiene `-1` en todas las casillas ocultas (no distingue minas de seguras). Solo al terminar aparece
  `mines`.
- **Si falla, reporten:** el texto exacto. Si ven una mina oculta, o la semilla, es un fallo de seguridad.
- **Para pensar:** ¿por qué no basta con ocultar las minas si se enviara la semilla?

### T14. Versión del protocolo

- **Pasos:** en el cliente A cambien `GameProtocol.Version` a `2` en `Assets/Scripts/Online/GameProtocol.cs`
  (no en el servidor) y pulsen CREATE ROOM.
- **Esperado:** el cliente muestra "Tu versión del juego no coincide con el servidor. Actualiza el juego." y **no**
  intenta pintar la respuesta. Después devuelvan la constante a `1`.
- **Reporten:** qué mensaje salió.

### T15. Trampa desde el cliente (opcional, para los curiosos)

- **Idea:** intenten hacer trampa. Por ejemplo, desde un script de prueba llamen a `PlayMove` con `kind = "timeout"`
  o con una casilla fuera del tablero (`cell = 9999`), o con el `matchId` de una partida ajena.
- **Esperado:** el servidor rechaza todo (`invalid_move`, `invalid_cell`, error "No participas en esta partida").
- **Reporten:** cualquier cosa que el servidor **acepte** y no debería.

---

## Lo que ya sabemos que NO funciona o falta (no hace falta reportarlo)

- El rating y las estadísticas **siguen escribiéndose desde el cliente** en la clase *Default* de Cloud Save (falsificables).
- El emparejamiento es por **código de sala**, no con Matchmaker.
- No hay barrido de partidas abandonadas ni limpieza de salas viejas.
- Los tiempos (20 s por turno, 180 s de inactividad) son constantes; no están en Remote Config.
- El cliente no recuerda el código de sala al reabrir la app.
