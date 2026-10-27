using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.CloudCode;
using Unity.Services.CloudCode.Subscriptions;
using Unity.Services.Core;
using UnityEngine;

namespace CloudGame.Online
{
    /// <summary>
    /// Habla con el módulo de Cloud Code <c>WipedOut</c>. El cliente NO decide nada del juego:
    /// envía intenciones (crear, unirse, revelar, avanzar, abandonar) y pinta la vista que
    /// devuelve el servidor.
    ///
    /// Cómo se entera de lo que hace el rival (mensajes):
    ///  1. <b>Push de Cloud Code</b>: tras cada jugada el servidor envía un mensaje LIVIANO
    ///     (<see cref="PushMessageType"/>, <c>{ type, matchId }</c>) que NUNCA lleva el estado. Este
    ///     cliente no se fía de su contenido: lo usa solo como señal para volver a pedir
    ///     <c>GetMatchState</c>.
    ///  2. <b>GetMatchState</b>: se llama al recibir el aviso, al entrar, al reconectar y como
    ///     sondeo de respaldo (~10 s). Un push se puede perder; el estado leído no.
    /// </summary>
    public sealed class OnlineMatchService
    {
        public const string ModuleName = "WipedOut";
        public const string PushMessageType = "match_updated";

        // Tipos de jugada que entiende PlayMove (los mismos textos que MoveKind en el servidor).
        private const string KindReveal = "reveal";
        private const string KindAdvance = "advance";
        private const string KindForfeit = "forfeit";

        private readonly ConcurrentQueue<string> incoming = new ConcurrentQueue<string>();
        private readonly Dictionary<string, string> pendingRequestIds = new Dictionary<string, string>();

        private ISubscriptionEvents subscription;
        private bool busy;
        private volatile bool refreshRequested;

        /// <summary>Contenido del push: solo "algo cambió en esta partida".</summary>
        [Serializable]
        private class PushPayload
        {
            public string type;
            public string matchId;
        }

        /// <summary>
        /// Llegó un aviso del servidor y falta pedir el estado real. Quien lo vea debe llamar a
        /// <see cref="RefreshAsync"/> en cuanto el servicio esté libre.
        /// </summary>
        public bool HasPendingRefresh => refreshRequested;

        /// <summary>Última vista aceptada. Null mientras no hay partida.</summary>
        public OnlineMatchView Current { get; private set; }

        /// <summary>Marca (Time.realtimeSinceStartup) del momento en que llegó <see cref="Current"/>.</summary>
        public float CurrentReceivedAt { get; private set; }

        public bool IsBusy => busy;

        public bool IsSubscribed { get; private set; }

        /// <summary>Hay una vista nueva (siempre de una respuesta del servidor; los push no llevan estado).</summary>
        public event Action<OnlineMatchView> ViewChanged;

        /// <summary>Algo falló de verdad (red, servidor, sesión). Los rechazos del juego NO llegan aquí.</summary>
        public event Action<string> Failed;

        /// <summary>Se perdió la conexión de push y volvió: conviene pedir el estado real.</summary>
        public event Action ResyncRequested;

        public bool IsReady =>
            UnityServices.State == ServicesInitializationState.Initialized
            && AuthenticationService.Instance.IsSignedIn;

        // ------------------------------------------------------------------ acciones

        public Task<OnlineMatchView> CreateAsync()
        {
            return CallAsync("CreateMatch", "create", new Dictionary<string, object>());
        }

        public Task<OnlineMatchView> JoinAsync(string matchCode)
        {
            string code = (matchCode ?? string.Empty).Trim().ToUpperInvariant();
            return CallAsync(
                "JoinMatch",
                "join:" + code,
                new Dictionary<string, object> { { "matchId", code } });
        }

        public Task<OnlineMatchView> RevealAsync(int cellIndex)
        {
            // La clave identifica ESTA jugada (turno + casilla): un reintento reutiliza el mismo
            // requestId y el servidor lo reconoce; una jugada distinta usa uno nuevo.
            return PlayMoveAsync(KindReveal, cellIndex, "reveal:" + Current?.turnNumber + ":" + cellIndex);
        }

        public Task<OnlineMatchView> AdvanceLevelAsync()
        {
            return PlayMoveAsync(KindAdvance, -1, "advance:" + Current?.turnNumber);
        }

        public Task<OnlineMatchView> ForfeitAsync()
        {
            return PlayMoveAsync(KindForfeit, -1, "forfeit:" + Current?.matchCode);
        }

        private Task<OnlineMatchView> PlayMoveAsync(string kind, int cell, string actionKey)
        {
            if (Current == null)
            {
                return Task.FromResult<OnlineMatchView>(null);
            }

            return CallAsync(
                "PlayMove",
                actionKey,
                new Dictionary<string, object>
                {
                    { "matchId", Current.matchCode },
                    { "expectedTurnNumber", Current.turnNumber },
                    { "kind", kind },
                    { "cell", cell },
                });
        }

        /// <summary>
        /// Pide el estado real (<c>GetMatchState</c>). Es lo que se llama al recibir un aviso,
        /// al entrar, al reconectar y como sondeo de respaldo. También despierta los plazos
        /// vencidos y el abandono del rival: el servidor no tiene relojes, así que cualquiera de
        /// los dos jugadores que pregunta los resuelve.
        /// </summary>
        public Task<OnlineMatchView> RefreshAsync()
        {
            if (Current == null)
            {
                return Task.FromResult<OnlineMatchView>(null);
            }

            refreshRequested = false;
            return CallAsync(
                "GetMatchState",
                null,
                new Dictionary<string, object> { { "matchId", Current.matchCode } });
        }

        /// <summary>Olvida la partida actual (no abandona: para eso está <see cref="ForfeitAsync"/>).</summary>
        public void Leave()
        {
            Current = null;
            refreshRequested = false;
            pendingRequestIds.Clear();
        }

        // --------------------------------------------------------------------- push

        /// <summary>Se suscribe a los mensajes que el servidor envía a ESTE jugador. Es seguro llamarlo varias veces.</summary>
        public async Task SubscribeAsync()
        {
            if (subscription != null || !IsReady)
            {
                return;
            }

            var callbacks = new SubscriptionEventCallbacks();
            callbacks.MessageReceived += OnPushReceived;
            callbacks.ConnectionStateChanged += OnConnectionStateChanged;
            callbacks.Kicked += OnKicked;
            callbacks.Error += error => Debug.LogWarning("[OnlineMatchService] Error de suscripción: " + error);

            try
            {
                subscription = await CloudCodeService.Instance.SubscribeToPlayerMessagesAsync(callbacks);
            }
            catch (Exception exception)
            {
                subscription = null;
                Debug.LogWarning("[OnlineMatchService] No se pudo suscribir a los mensajes push: " + exception.Message);
                Failed?.Invoke("Sin avisos en tiempo real; se usará la consulta periódica.");
            }
        }

        public async Task UnsubscribeAsync()
        {
            ISubscriptionEvents current = subscription;
            subscription = null;
            IsSubscribed = false;

            if (current != null)
            {
                try
                {
                    await current.UnsubscribeAsync();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[OnlineMatchService] Error al cancelar la suscripción: " + exception.Message);
                }
            }
        }

        /// <summary>
        /// Los callbacks del SDK pueden llegar en otro hilo, así que solo se encola el texto y
        /// se procesa aquí. Hay que llamarlo desde <c>Update</c>.
        /// </summary>
        public void Pump()
        {
            while (incoming.TryDequeue(out string json))
            {
                // El aviso NO se usa para pintar nada: solo dice "algo cambió en esta partida".
                // El estado real se vuelve a pedir con GetMatchState (RefreshAsync).
                PushPayload payload = ParsePush(json);
                if (payload != null && Current != null && payload.matchId == Current.matchCode)
                {
                    refreshRequested = true;
                }
            }
        }

        private void OnPushReceived(IMessageReceivedEvent message)
        {
            if (message != null && message.MessageType == PushMessageType && !string.IsNullOrEmpty(message.Message))
            {
                incoming.Enqueue(message.Message);
            }
        }

        private static PushPayload ParsePush(string json)
        {
            try
            {
                PushPayload payload = JsonUtility.FromJson<PushPayload>(json);
                return payload != null && payload.type == PushMessageType && !string.IsNullOrEmpty(payload.matchId)
                    ? payload
                    : null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private void OnConnectionStateChanged(EventConnectionState state)
        {
            IsSubscribed = state == EventConnectionState.Subscribed;

            // Si se perdió el hilo de avisos, es posible que nos hayamos perdido jugadas.
            if (state == EventConnectionState.Subscribed || state == EventConnectionState.Unsynced)
            {
                ResyncRequested?.Invoke();
            }
        }

        private void OnKicked()
        {
            // El servidor nos desuscribió: se vuelve a pedir en el siguiente SubscribeAsync.
            subscription = null;
            IsSubscribed = false;
        }

        // ------------------------------------------------------------------ llamadas

        private async Task<OnlineMatchView> CallAsync(
            string function, string actionKey, Dictionary<string, object> args)
        {
            if (!IsReady)
            {
                Failed?.Invoke("Inicia sesión antes de jugar online.");
                return null;
            }

            if (busy)
            {
                return null;
            }

            if (actionKey != null)
            {
                args["requestId"] = RequestIdFor(actionKey);
            }

            busy = true;
            try
            {
                string json = await CloudCodeService.Instance.CallModuleEndpointAsync<string>(
                    ModuleName, function, args);

                OnlineMatchView view = Parse(json);
                if (view == null)
                {
                    Failed?.Invoke("Respuesta del servidor ilegible.");
                    return null;
                }

                // Si el servidor habla otra versión del protocolo no se interpreta la respuesta:
                // mejor avisar que pintar algo que no significa lo que parece.
                if (view.protocolVersion != GameProtocol.Version)
                {
                    Failed?.Invoke("Tu versión del juego no coincide con el servidor. Actualiza el juego.");
                    return null;
                }

                // Llegó una respuesta (aunque sea un rechazo): esa jugada ya no hay que reintentarla.
                // Con "conflict" se conserva el requestId: el servidor no la aplicó y se reintenta igual.
                if (actionKey != null && view.outcome != OnlineOutcome.Conflict)
                {
                    pendingRequestIds.Remove(actionKey);
                }

                Accept(view);
                return view;
            }
            catch (CloudCodeRateLimitedException exception)
            {
                Failed?.Invoke("Demasiadas peticiones. Espera " + exception.RetryAfter + " s.");
                return null;
            }
            catch (CloudCodeException exception)
            {
                // Sin respuesta: NO se borra el requestId. Si el servidor sí aplicó la jugada, el
                // reintento con el mismo id devolverá "replayed" en vez de contarla dos veces.
                Failed?.Invoke(Describe(exception));
                return null;
            }
            catch (RequestFailedException exception)
            {
                Failed?.Invoke("Servicio no disponible (" + exception.ErrorCode + ").");
                return null;
            }
            catch (Exception exception)
            {
                Failed?.Invoke(exception.Message);
                return null;
            }
            finally
            {
                busy = false;
            }
        }

        private string RequestIdFor(string actionKey)
        {
            if (!pendingRequestIds.TryGetValue(actionKey, out string id))
            {
                id = Guid.NewGuid().ToString("N");
                pendingRequestIds[actionKey] = id;
            }

            return id;
        }

        /// <summary>
        /// Decide si una vista reemplaza a la actual. Una respuesta más vieja que lo que ya se ve
        /// (un número de turno menor) nunca debe pisar un estado más nuevo. El mismo turno sí se
        /// acepta: es el estado real y puede traer un plazo o un estado de sala distintos.
        /// </summary>
        private void Accept(OnlineMatchView view)
        {
            if (Current != null && Current.matchCode == view.matchCode)
            {
                bool older = view.turnNumber < Current.turnNumber;
                bool statusChanged = view.status != Current.status;

                if (older && !statusChanged)
                {
                    return;
                }
            }

            Current = view;
            CurrentReceivedAt = Time.realtimeSinceStartup;
            ViewChanged?.Invoke(view);
        }

        private static OnlineMatchView Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            try
            {
                OnlineMatchView view = JsonUtility.FromJson<OnlineMatchView>(json);
                return view != null && !string.IsNullOrEmpty(view.matchCode) ? view : null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static string Describe(CloudCodeException exception)
        {
            switch (exception.Reason)
            {
                case CloudCodeExceptionReason.NoInternetConnection:
                    return "Sin conexión. Tu jugada no se perdió: vuelve a intentarlo.";
                case CloudCodeExceptionReason.ScriptError:
                    // El servidor lanza estos errores a propósito (sala inexistente, no participas...).
                    return LastMeaningfulLine(exception.Message);
                default:
                    return "Error del servidor (" + exception.Reason + ").";
            }
        }

        private static string LastMeaningfulLine(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return "Error del servidor.";
            }

            string result = message;
            foreach (string line in message.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0 && !trimmed.StartsWith("at "))
                {
                    result = trimmed;
                }
            }

            return result;
        }
    }
}
