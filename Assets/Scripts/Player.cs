using UnityEngine;
using Unity.Netcode;
using TMPro; 
using UnityEngine.UI; 
using Unity.Netcode.Components; 
using System.Collections;
using System.Collections.Generic;

// --- ENUMERACIONES GLOBALES ---
// Define los tipos de Power-Up disponibles en el juego.
public enum PowerUpType { Speed, FireRate, Health }

// --- CLASE PRINCIPAL DEL JUGADOR ---
// Componentes requeridos para el correcto funcionamiento del GameObject del jugador.
[RequireComponent(typeof(NetworkObject))]     // Para la identificación y gestión en red.
[RequireComponent(typeof(NetworkTransform))]  // Sincroniza posición, rotación y escala a través de la red.
[RequireComponent(typeof(Rigidbody))]         // Necesario para la física del jugador (movimiento, colisiones).
public class Player : NetworkBehaviour // Hereda de NetworkBehaviour para funcionalidades de Netcode for GameObjects.
{
    // --- SECCIÓN: Variables Miembro (Configurables en Inspector) ---

    [Header("Referencias (Asignar en Inspector)")]
    [SerializeField] private GameObject healthBarPrefab; // Prefab para la barra de vida flotante (UI en World Space).
    [SerializeField] private Transform healthBarAnchor; // Punto de anclaje para la barra de vida flotante.
    [SerializeField] private MeshRenderer playerMeshRenderer; // Renderer principal para cambiar el color del jugador.
    [SerializeField] private Transform firePoint; // Punto desde donde se originan los disparos.
    [SerializeField] private GameObject bulletPrefab; // Prefab de la bala que se dispara.
    [Tooltip("Prefab del efecto visual al impactar la bala. Si VFXManager tiene uno asignado, ese se usará prioritariamente.")]
    [SerializeField] private GameObject bulletImpactEffectPrefab; // Efecto visual para el impacto de bala (ahora gestionado por VFXManager).
    [Tooltip("Prefab del efecto visual al morir el jugador. Si VFXManager tiene uno asignado, ese se usará prioritariamente.")]
    [SerializeField] private GameObject deathEffectPrefab; // Efecto visual para la muerte (ahora gestionado por VFXManager).
    [SerializeField] private GameObject dashEffectPrefab; // Efecto visual para la habilidad de dash.

    [Header("Cámara Primera Persona")]
    [SerializeField] private Camera playerCamera; // Cámara principal asignada a este jugador.
    [SerializeField] private AudioListener audioListener; // Componente para escuchar sonidos desde la perspectiva de este jugador.
    [SerializeField] private PlayerLook playerLookScript; // Script que maneja la rotación de la cámara basada en el input del ratón.

    [Header("Movimiento")]
    [SerializeField] private float moveSpeed = 5.0f; // Velocidad base de movimiento del jugador.

    [Header("Rotación Cámara (Autoridad Servidor)")]
    [SerializeField] private float serverMouseSensitivity = 100f; // Sensibilidad del ratón aplicada en el servidor.
    [SerializeField] private float minVerticalAngle = -90f; // Límite inferior para la rotación vertical de la cámara.
    [SerializeField] private float maxVerticalAngle = 90f; // Límite superior para la rotación vertical de la cámara.

    [Header("Vida")]
    [SerializeField] private int maxHealth = 100; // Salud máxima del jugador.
    [SerializeField] private Color deadColor = Color.gray; // Color que adopta el jugador al morir.

    [Header("Disparo")]
    [SerializeField] private float fireRate = 0.5f; // Tiempo mínimo en segundos entre disparos (cadencia).
    private float nextFireTime = 0f; // Control interno para el cooldown del disparo.

    [Header("Interacción (Puertas)")]
    [SerializeField] private float interactionRadius = 2.0f; // Radio en el que el jugador puede interactuar con objetos.
    [SerializeField] private LayerMask interactableLayer; // Capa física para objetos interactuables (ej. puertas).

    [Header("Power-Ups")]
    [SerializeField] private float powerUpDuration = 10.0f; // Duración estándar de los efectos de power-up.
    [SerializeField] private float speedBoostMultiplier = 1.5f; // Multiplicador de velocidad para el power-up de velocidad.
    [SerializeField] private float fireRateBoostMultiplier = 2f; // Multiplicador de cadencia (divide fireRate base).
    [SerializeField] private int healthBoostAmount = 50; // Cantidad de vida recuperada por el power-up de salud.
    [SerializeField] private GameObject speedEffectVisual; // Efecto visual local para el power-up de velocidad.
    [SerializeField] private GameObject fireRateEffectVisual; // Efecto visual local para el power-up de cadencia.
    private List<ActivePowerUp> activePowerUps = new List<ActivePowerUp>(); // Lista para rastrear power-ups activos localmente.

    [Header("Mecánica Nueva (Dash)")]
    [SerializeField] private float dashDistance = 5f; // Distancia que recorre el jugador al hacer dash.
    [SerializeField] private float dashCooldown = 2f; // Tiempo de espera entre usos del dash.
    [SerializeField] private float dashDuration = 0.2f; // Duración del movimiento rápido del dash.
    private float nextDashTime = 0f; // Control interno para el cooldown del dash.
    private bool isDashing = false; // Estado para indicar si el jugador está actualmente en un dash.

    // --- SECCIÓN: Network Variables ---
    // Variables sincronizadas a través de la red. El servidor tiene autoridad para escribir.
    public NetworkVariable<Color> NetworkColor = new NetworkVariable<Color>(Color.white, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server); // Color visual del jugador.
    public NetworkVariable<int> NetworkSalud = new NetworkVariable<int>(100, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server); // Salud actual del jugador.
    public NetworkVariable<bool> IsDead = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server); // Estado de muerte del jugador.
    private NetworkVariable<float> networkVerticalRotation = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server); // Rotación vertical de la cámara (pitch).

    // --- SECCIÓN: Referencias Internas y Estado Local ---
    private TMP_Text textoVida; // Referencia al componente TextMeshPro para mostrar la vida en la UI local.
    private Slider healthBarSlider; // Referencia al Slider de la barra de vida flotante.
    private GameObject healthBarInstance; // Instancia del prefab de la barra de vida flotante.
    private Rigidbody rb; // Componente Rigidbody del jugador.
    private float baseMoveSpeed; // Velocidad de movimiento original (para revertir power-ups).
    private float baseFireRate; // Cadencia de disparo original (para revertir power-ups).

    // Propiedades públicas para acceso de solo lectura a ciertos valores.
    public int MaxHealth => maxHealth;
    public int HealthBoostAmount => healthBoostAmount;

    // Estructura para gestionar la información de los power-ups activos localmente.
    private struct ActivePowerUp {
        public PowerUpType Type; // Tipo de power-up.
        public Coroutine ExpiryCoroutine; // Corutina que gestiona la expiración del efecto visual.
        public GameObject VisualEffect;   // GameObject del efecto visual asociado.
    }

    // --- SECCIÓN: Métodos de Ciclo de Vida (Monobehaviour / NetworkBehaviour) ---
    // Se llama cuando el NetworkObject del jugador es spawneado en la red.
    public override void OnNetworkSpawn()
    {
        // Cachear componentes locales.
        rb = GetComponent<Rigidbody>();
        playerLookScript = GetComponent<PlayerLook>();

        // Guardar valores base para restaurarlos después de los power-ups.
        baseMoveSpeed = moveSpeed;
        baseFireRate = fireRate;

        // Aplicar estado visual inicial basado en NetworkVariables (importante para late joiners).
        OnIsDeadChanged(false, IsDead.Value); // Actualiza el estado de muerte.
        ColorChanged(NetworkColor.Value, NetworkColor.Value); // Actualiza el color.

        // Suscribirse a los eventos de cambio de las NetworkVariables.
        NetworkColor.OnValueChanged += ColorChanged;
        NetworkSalud.OnValueChanged += OnHealthChanged;
        IsDead.OnValueChanged += OnIsDeadChanged;

        // Suscripción a la rotación vertical solo en clientes no-servidores.
        if (!IsServer)
        {
            networkVerticalRotation.OnValueChanged += HandleVerticalRotationChanged;
        }
        // Aplicar rotación vertical inicial.
        if (playerLookScript != null)
        {
            playerLookScript.SetVerticalRotationLocally(networkVerticalRotation.Value);
        }

        // Lógica específica según si este script es del jugador local (IsOwner) o de un jugador remoto.
        if (IsOwner)
        {
            // Habilitar cámara y listener para el jugador local.
            if (playerCamera != null) playerCamera.enabled = true;
            if (audioListener != null) audioListener.enabled = true;
            // Desactivar cámara principal de la escena si existe y es diferente.
            Camera sceneCamera = Camera.main;
            if (sceneCamera != null && sceneCamera != playerCamera)
            {
                sceneCamera.gameObject.SetActive(false);
            }
            // Configurar UI local.
            SetupLocalUI();
        }
        else // Es un jugador remoto.
        {
            // Deshabilitar cámara y listener.
            if (playerCamera != null) playerCamera.enabled = false;
            if (audioListener != null) audioListener.enabled = false;
            // Configurar barra de vida flotante para el avatar remoto.
            SetupFloatingHealthBar();
        }

        VerifyNetworkTransformConfig(); // Verificar configuración de NetworkTransform (meramente informativo).
        // Ajustar físicas: clientes no-servidores y sin NetworkRigidbody deben tener Rigidbody kinemático.
        var netRigidbody = GetComponent<NetworkRigidbody>();
        if (!IsServer && netRigidbody == null)
        {
            rb.isKinematic = true;
        }
        // Si está muerto, siempre kinemático.
        if (IsDead.Value) rb.isKinematic = true;
    }

    // Se llama cada frame. Usado para leer input local si somos el propietario (IsOwner).
    void Update()
    {
        // Ignorar si no somos el propietario, estamos muertos o en medio de un dash.
        if (!IsOwner || IsDead.Value || isDashing) return;

        // Procesar input de movimiento.
        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");
        Vector3 moveDir = new Vector3(h, 0, v);
        if (moveDir != Vector3.zero) SubmitMovementRequestServerRpc(moveDir.normalized); // Enviar petición de movimiento al servidor.

        // Procesar input de disparo.
        if (Input.GetButton("Fire1") && Time.time >= nextFireTime)
        {
            nextFireTime = Time.time + fireRate; // Aplicar cooldown.
            if (firePoint != null) {
                SubmitFireRequestServerRpc(firePoint.position, firePoint.rotation); // Enviar petición de disparo al servidor.
            } else { Debug.LogError($"Player {OwnerClientId}: Fire Point no está asignado."); }
        }

        // Procesar input de interacción.
        if (Input.GetKeyDown(KeyCode.E))
        {
            SubmitInteractionRequestServerRpc(); // Enviar petición de interacción al servidor.
        }

        // Procesar input de dash.
        if (Input.GetKeyDown(KeyCode.LeftShift) && Time.time >= nextDashTime)
        {
             Vector3 dashDir = moveDir.normalized; // Dirección del dash basada en el movimiento actual.
             if (dashDir == Vector3.zero) dashDir = transform.forward; // Si no hay movimiento, dash hacia adelante.
             nextDashTime = Time.time + dashCooldown; // Aplicar cooldown.
             SubmitDashRequestServerRpc(dashDir); // Enviar petición de dash al servidor.
        }
        // El input de cámara es manejado por PlayerLook.cs y enviado al servidor desde allí.
    }

    // Se llama cuando el NetworkObject es despawneado. Limpieza de suscripciones y recursos.
    public override void OnNetworkDespawn()
    {
        // Desuscribirse de eventos de NetworkVariables.
        if (NetworkColor != null) NetworkColor.OnValueChanged -= ColorChanged;
        if (NetworkSalud != null) NetworkSalud.OnValueChanged -= OnHealthChanged;
        if (IsDead != null) IsDead.OnValueChanged -= OnIsDeadChanged;
        if (networkVerticalRotation != null && !IsServer)
        {
            networkVerticalRotation.OnValueChanged -= HandleVerticalRotationChanged;
        }

        // Destruir instancias de UI.
        if (healthBarInstance != null) Destroy(healthBarInstance);
        // Detener corutinas y limpiar efectos visuales de power-ups.
        StopAllPowerupCoroutinesAndVisuals();
        base.OnNetworkDespawn(); // Llamar a la implementación base.
    }

    // --- SECCIÓN: Configuración Inicial y UI Local ---
    // Configura la UI específica del jugador local (ej. texto de vida).
    private void SetupLocalUI()
    {
        var vidaPlayerObject = GameObject.Find("VidaPlayer"); // Busca el objeto UI por nombre.
        if (vidaPlayerObject != null) {
            textoVida = vidaPlayerObject.GetComponent<TMP_Text>();
            if (textoVida != null) UpdateLocalHealthUI(NetworkSalud.Value); // Actualiza UI con vida inicial.
            else Debug.LogError($"Player {OwnerClientId}: No se encontró TMP_Text en 'VidaPlayer'");
        } else Debug.LogError($"Player {OwnerClientId}: No se encontró 'VidaPlayer' en la escena.");
    }

    // Configura la barra de vida flotante para avatares remotos.
    private void SetupFloatingHealthBar()
    {
        if (healthBarPrefab != null && healthBarAnchor != null) {
            healthBarInstance = Instantiate(healthBarPrefab, healthBarAnchor.position, healthBarAnchor.rotation);
            healthBarInstance.transform.SetParent(healthBarAnchor, true); // Emparentar al anchor.
            healthBarSlider = healthBarInstance.GetComponentInChildren<Slider>();
            if (healthBarSlider != null) UpdateFloatingHealthBar(NetworkSalud.Value); // Actualiza barra con vida inicial.
            else Debug.LogError($"Player {OwnerClientId}: Prefab de barra de vida no tiene Slider.");
        } else Debug.LogError($"Player {OwnerClientId}: Falta healthBarPrefab o healthBarAnchor en Inspector.");
    }

    // Verifica (informativamente) la configuración de NetworkTransform.
     private void VerifyNetworkTransformConfig() {
        var networkTransform = GetComponent<NetworkTransform>();
        if (networkTransform == null) Debug.LogError($"Player {OwnerClientId}: NetworkTransform no encontrado.");
        // En NGO, NetworkTransform es server-authoritative por defecto. ClientNetworkTransform es para autoridad del cliente.
     }

    // --- SECCIÓN: Callbacks de Network Variables ---
    // Se ejecutan en todos los clientes cuando el valor de una NetworkVariable cambia en el servidor.

    // Callback para cambios en NetworkSalud.
    private void OnHealthChanged(int previousValue, int newValue)
    {
        if (IsOwner) UpdateLocalHealthUI(newValue); // Actualiza UI local.
        else UpdateFloatingHealthBar(newValue); // Actualiza barra de vida flotante.
    }

    // Callback para cambios en NetworkColor.
    private void ColorChanged(Color previousValue, Color newValue)
    {
        if (playerMeshRenderer != null)
        {
            // El color de muerto tiene prioridad.
            playerMeshRenderer.material.color = IsDead.Value ? deadColor : newValue;
        }
    }

    // Callback para cambios en IsDead.
    private void OnIsDeadChanged(bool previousValue, bool newValue)
    {
        bool justDied = newValue && !previousValue; // Transición a muerto.
        bool justRespawned = !newValue && previousValue; // Transición a vivo.

        // Actualización visual.
        if (playerMeshRenderer != null)
        {
            playerMeshRenderer.material.color = newValue ? deadColor : NetworkColor.Value;
            playerMeshRenderer.enabled = !newValue || IsOwner; // Ocultar si muerto y no es el dueño.
        }
        if (healthBarInstance != null) healthBarInstance.SetActive(!newValue); // Ocultar barra de vida si muerto.

        // Actualización física.
        if (rb != null) {
            // Rigidbody kinemático si muerto o cliente no-servidor sin NetworkRigidbody.
            bool shouldBeKinematic = newValue || (!IsServer && GetComponent<NetworkRigidbody>() == null);
            if (rb.isKinematic != shouldBeKinematic)
            {
                 rb.isKinematic = shouldBeKinematic;
            }
            if (justDied) { // Si acaba de morir.
                rb.linearVelocity = Vector3.zero; // Detener movimiento.
                rb.angularVelocity = Vector3.zero; // Detener rotación.
            }
        }

        if (justDied) {
            // Llama al VFXManager para mostrar el efecto de muerte en todos los clientes.
            if (IsServer) // Solo el servidor invoca el RPC.
            {
                if (VFXManager.Instance != null)
                {
                    VFXManager.Instance.PlayPlayerDeathClientRpc(transform.position, transform.rotation);
                }
                else
                {
                    Debug.LogError("Player: VFXManager.Instance no encontrado. No se puede mostrar el efecto de muerte.", this);
                }
            }

            if (IsOwner) { // Si somos el dueño del jugador muerto.
                 Debug.Log("¡Has Muerto!");
                 StopAllPowerupCoroutinesAndVisuals(); // Detener efectos de power-ups locales.
            }
        }

        if (justRespawned) { // Si acabamos de revivir.
            if (IsOwner) {
                 Debug.Log("¡Has Revivido!");
            }
        }
    }

    // Callback para cambios en la rotación vertical de la cámara (sincronizada).
    private void HandleVerticalRotationChanged(float previousValue, float newValue)
    {
        if (playerLookScript != null)
        {
            playerLookScript.SetVerticalRotationLocally(newValue); // Aplica la rotación al script de cámara local.
        }
    }

    // --- SECCIÓN: Actualización de UI / Visual ---
    // Actualiza el texto de la UI local con la vida actual.
    private void UpdateLocalHealthUI(int currentHealth) {
        if (textoVida != null) textoVida.text = "HEALTH: " + currentHealth.ToString();
    }

    // Actualiza el slider de la barra de vida flotante.
    private void UpdateFloatingHealthBar(int currentHealth) {
        if (healthBarSlider != null) healthBarSlider.value = (float)currentHealth / maxHealth; // Como porcentaje.
        if (healthBarInstance != null) healthBarInstance.SetActive(currentHealth > 0 && !IsDead.Value); // Visible si vivo.
    }

    // --- SECCIÓN: Server RPCs (Cliente -> Servidor) ---
    // Métodos que los clientes pueden llamar, pero se ejecutan en el servidor.

    // RPC opcional para solicitar datos iniciales (actualmente gestionado por GameManager).
    [ServerRpc]
    private void RequestInitialDataServerRpc(ServerRpcParams rpcParams = default)
    {
        ulong clientId = rpcParams.Receive.SenderClientId;
        // Este RPC puede estar vacío si GameManager maneja toda la inicialización.
    }

    // RPC para solicitar movimiento al servidor.
    [ServerRpc]
    void SubmitMovementRequestServerRpc(Vector3 direction, ServerRpcParams rpcParams = default)
    {
        if (IsDead.Value || isDashing) return; // Ignorar si muerto o en dash.
        // Mover usando Rigidbody.MovePosition para mejor manejo de colisiones (si no es kinemático).
        if (rb != null && !rb.isKinematic)
        {
            // Mover en la dirección relativa al jugador, pero en el espacio del mundo.
            rb.MovePosition(rb.position + transform.TransformDirection(direction) * moveSpeed * Time.deltaTime);
        }
        else // Fallback a transform.Translate si es kinemático o no hay Rigidbody.
        {
             transform.Translate(transform.TransformDirection(direction) * moveSpeed * Time.deltaTime, Space.World);
        }
    }

    // RPC para actualizar la rotación de la cámara en el servidor.
    [ServerRpc]
    public void UpdateLookInputServerRpc(float mouseXDelta, float mouseYDelta, ServerRpcParams rpcParams = default)
    {
        if (IsDead.Value || isDashing) return; // Ignorar si muerto o en dash.
        // Rotación horizontal del cuerpo del jugador.
        transform.Rotate(Vector3.up * mouseXDelta * serverMouseSensitivity * Time.deltaTime);
        // Calcular y aplicar rotación vertical (pitch) a la NetworkVariable.
        float currentVertical = networkVerticalRotation.Value;
        currentVertical -= mouseYDelta * serverMouseSensitivity * Time.deltaTime;
        currentVertical = Mathf.Clamp(currentVertical, minVerticalAngle, maxVerticalAngle); // Limitar ángulo.
        networkVerticalRotation.Value = currentVertical; // Actualizar variable sincronizada.
        // Si el servidor es también un cliente (Host), aplicar rotación vertical localmente para evitar latencia.
         if (IsHost && playerLookScript != null)
         {
             playerLookScript.SetVerticalRotationLocally(currentVertical);
         }
    }

    // RPC para solicitar un disparo al servidor.
    [ServerRpc]
    void SubmitFireRequestServerRpc(Vector3 spawnPos, Quaternion spawnRot, ServerRpcParams rpcParams = default)
    {
        if (IsDead.Value || isDashing) return; // Ignorar si muerto o en dash.
        ulong clientId = rpcParams.Receive.SenderClientId;

        if (bulletPrefab != null)
        {
            GameObject bulletInstance = Instantiate(bulletPrefab, spawnPos, spawnRot); // Instanciar bala.
            NetworkObject bulletNetworkObject = bulletInstance.GetComponent<NetworkObject>();
            Bullet bulletScript = bulletInstance.GetComponent<Bullet>();

            if (bulletNetworkObject != null && bulletScript != null)
            {
                Color jugadorColorActual = playerMeshRenderer != null ? playerMeshRenderer.material.color : NetworkColor.Value;
                // Inicializar la bala. El prefab de impacto ahora es opcional/null aquí ya que VFXManager lo maneja.
                bulletScript.Initialize(OwnerClientId, jugadorColorActual, null);
                bulletNetworkObject.Spawn(true); // Spawnear la bala en la red.
            }
            else // Error en configuración del prefab de bala.
            {
                 Debug.LogError($"Player {clientId}: Prefab de bala ({bulletPrefab.name}) no tiene NetworkObject o Bullet script.");
                 Destroy(bulletInstance); // Destruir instancia local fallida.
            }
        }
        else // Prefab de bala no asignado.
        {
             Debug.LogError($"Player {clientId}: Prefab de bala no asignado en el Inspector.");
        }
    }

    // RPC para solicitar una interacción (ej. abrir puerta) al servidor.
    [ServerRpc]
    void SubmitInteractionRequestServerRpc(ServerRpcParams rpcParams = default)
    {
        if (IsDead.Value || isDashing) return; // Ignorar si muerto o en dash.
        // Buscar objetos interactuables cercanos.
        Collider[] hits = Physics.OverlapSphere(transform.position, interactionRadius, interactableLayer);
        GameObject closestInteractable = null;
        float minDistance = float.MaxValue;
        foreach (var hitCollider in hits)
        {
            Door door = hitCollider.GetComponent<Door>(); // Asume que los interactuables tienen un script 'Door'.
            if (door != null)
            {
                // Encontrar el interactuable más cercano.
                float distance = Vector3.Distance(transform.position, hitCollider.transform.position);
                if (distance < minDistance) {
                    minDistance = distance;
                    closestInteractable = hitCollider.gameObject;
                }
            }
        }
        if (closestInteractable != null) // Si se encontró uno.
        {
            Door doorToToggle = closestInteractable.GetComponent<Door>();
            if (doorToToggle != null) {
                doorToToggle.ToggleDoorState(); // Llamar al método para cambiar estado de la puerta.
            }
        }
    }

    // RPC para solicitar un dash al servidor.
    [ServerRpc]
    void SubmitDashRequestServerRpc(Vector3 direction)
    {
        if (IsDead.Value || isDashing) return; // Ignorar si muerto o en dash.
        StartCoroutine(PerformDashServer(direction)); // Ejecutar lógica de dash en servidor.
        NotifyDashClientRpc(direction); // Notificar a clientes para efecto visual.
    }

    // --- SECCIÓN: Client RPCs (Servidor -> Cliente(s)) ---
    // Métodos que el servidor puede llamar para que se ejecuten en clientes específicos.

    // RPC para teletransportar forzosamente al cliente propietario.
    [ClientRpc]
    private void TeleportClientRpc(Vector3 position, ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return; // Ejecutar solo en el cliente propietario.
        // Mover Rigidbody si existe y no es kinemático, sino mover transform.
        if (rb != null && !rb.isKinematic) rb.position = position;
        else transform.position = position;
    }

    // RPC para mostrar el efecto visual del dash en todos los clientes.
    [ClientRpc]
    void NotifyDashClientRpc(Vector3 direction)
    {
        if (dashEffectPrefab != null) { // Si hay un prefab de efecto asignado.
             GameObject fx = Instantiate(dashEffectPrefab, transform.position, Quaternion.LookRotation(direction)); // Instanciar efecto.
             Destroy(fx, 1.0f); // Autodestruir efecto después de un tiempo.
        }
    }

    // RPC para notificar al cliente propietario que ha recogido un power-up.
    [ClientRpc]
    private void NotifyPowerUpPickupClientRpc(PowerUpType type, ClientRpcParams clientRpcParams = default)
    {
        if (!IsOwner) return; // Ejecutar solo en el cliente propietario.
        StartCoroutine(HandlePowerUpEffectLocal(type)); // Iniciar manejo local del efecto visual.
    }

    // --- SECCIÓN: Lógica de Juego (Vida) ---
    // Aplica daño al jugador (ejecutado en servidor).
    public void TakeDamage(int amount)
    {
        if (!IsServer || IsDead.Value || isDashing) return; // Solo servidor, y si vivo y no en dash.
        int previousHealth = NetworkSalud.Value;
        NetworkSalud.Value = Mathf.Max(0, NetworkSalud.Value - amount); // Restar vida, mínimo 0.
        if (NetworkSalud.Value <= 0 && previousHealth > 0) { // Si la vida llega a 0.
            Die(); // Llamar a la lógica de muerte.
        }
    }

    // Lógica de muerte del jugador (ejecutado en servidor).
    private void Die()
    {
        if (!IsServer || IsDead.Value) return; // Seguridad extra.
        IsDead.Value = true; // Establecer NetworkVariable para sincronizar estado de muerte.
    }

    // Corutina para un temporizador de respawn (ejemplo, no usada activamente).
    private IEnumerator RespawnTimer(float delay) {
         yield return new WaitForSeconds(delay);
         Respawn();
    }

    // Lógica de respawn del jugador (ejecutado en servidor).
    public void Respawn() {
        if (!IsServer) return;
        // Obtener posición de spawn (idealmente desde GameManager).
        Vector3 spawnPos = Vector3.up; // Posición por defecto.
        GameManager gm = GameManager.Instance; // Asume Singleton GameManager.
        if (gm != null) {
             spawnPos = gm.GetRandomSpawnPoint(); // Método para obtener punto de spawn aleatorio.
        }
        transform.position = spawnPos; // Reposicionar jugador.
        NetworkSalud.Value = maxHealth; // Restaurar salud.
        IsDead.Value = false; // Marcar como vivo.
    }

    // Método público para quitar vida (ej. para botón de debug en NetworkHUD).
    public void QuitarVida(int cantidad) {
        if (IsServer) TakeDamage(cantidad); // Asegurar que solo el servidor procese el daño.
    }

    // --- SECCIÓN: Lógica de Power-Ups ---
    // Maneja la recogida de un power-up en el servidor.
    public void ServerHandlePowerUpPickup(PowerUpType type)
    {
        if (!IsServer || IsDead.Value) return; // Solo servidor y si vivo.
        ApplyPowerUpEffectOnServer(type); // Aplicar efecto lógico en servidor.
        // Enviar ClientRpc solo al cliente que recogió el power-up.
        ClientRpcParams clientRpcParams = new ClientRpcParams {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } }
        };
        NotifyPowerUpPickupClientRpc(type, clientRpcParams); // Notificar para feedback visual local.
    }

    // Aplica el efecto del power-up en el estado del servidor.
    private void ApplyPowerUpEffectOnServer(PowerUpType type)
    {
        if (!IsServer) return;
        switch (type) {
            case PowerUpType.Speed: StartCoroutine(ServerPowerUpDuration(type, powerUpDuration)); break; // Iniciar corutina de duración.
            case PowerUpType.FireRate: StartCoroutine(ServerPowerUpDuration(type, powerUpDuration)); break; // Iniciar corutina de duración.
            case PowerUpType.Health: NetworkSalud.Value = Mathf.Min(maxHealth, NetworkSalud.Value + healthBoostAmount); break; // Aplicar curación.
        }
    }

    // Corutina en servidor para manejar la duración de power-ups (Speed, FireRate).
    private IEnumerator ServerPowerUpDuration(PowerUpType type, float duration)
    {
        // Aplicar efecto.
        switch (type) {
            case PowerUpType.Speed: moveSpeed = baseMoveSpeed * speedBoostMultiplier; break;
            case PowerUpType.FireRate: fireRate = baseFireRate / fireRateBoostMultiplier; break;
        }
        yield return new WaitForSeconds(duration); // Esperar duración.
        // Revertir efecto.
        switch (type) {
            case PowerUpType.Speed: moveSpeed = baseMoveSpeed; break;
            case PowerUpType.FireRate: fireRate = baseFireRate; break;
        }
    }

    // Corutina en cliente propietario para manejar efectos visuales locales de power-ups.
    private IEnumerator HandlePowerUpEffectLocal(PowerUpType type)
    {
        GameObject visualEffectInstance = null;
        float currentDuration = powerUpDuration;
        // Desactivar efecto anterior del mismo tipo si existe.
        ActivePowerUp existingPowerup = activePowerUps.Find(p => p.Type == type);
        if (existingPowerup.VisualEffect != null || existingPowerup.ExpiryCoroutine != null)
        {
            if (existingPowerup.VisualEffect != null) existingPowerup.VisualEffect.SetActive(false);
            if (existingPowerup.ExpiryCoroutine != null) StopCoroutine(existingPowerup.ExpiryCoroutine);
            activePowerUps.Remove(existingPowerup);
        }
        // Activar efecto visual correspondiente.
        switch (type) {
            case PowerUpType.Speed: if (speedEffectVisual != null) speedEffectVisual.SetActive(true); visualEffectInstance = speedEffectVisual; break;
            case PowerUpType.FireRate: if (fireRateEffectVisual != null) fireRateEffectVisual.SetActive(true); visualEffectInstance = fireRateEffectVisual; break;
            case PowerUpType.Health: currentDuration = 0; /* Efecto instantáneo */ break;
        }
        // Registrar y manejar expiración visual.
        ActivePowerUp newActivePowerUp = new ActivePowerUp { Type = type, VisualEffect = visualEffectInstance };
        if (currentDuration > 0 || visualEffectInstance != null) {
            Coroutine expiry = StartCoroutine(RevertPowerUpEffectLocal(type, currentDuration, newActivePowerUp));
            newActivePowerUp.ExpiryCoroutine = expiry;
            activePowerUps.Add(newActivePowerUp);
        }
        yield return null;
    }

    // Corutina en cliente propietario para revertir efectos visuales locales.
    private IEnumerator RevertPowerUpEffectLocal(PowerUpType type, float duration, ActivePowerUp trackingInfo)
    {
        if (duration > 0) yield return new WaitForSeconds(duration);
        // Revertir si aún está activo.
        if (activePowerUps.Contains(trackingInfo))
        {
            if (trackingInfo.VisualEffect != null) trackingInfo.VisualEffect.SetActive(false);
            activePowerUps.Remove(trackingInfo);
        }
    }

    // Detiene todas las corutinas y efectos visuales de power-ups locales.
     private void StopAllPowerupCoroutinesAndVisuals() {
         if (!IsOwner) return; // Solo para el propietario.
         List<ActivePowerUp> powerupsToStop = new List<ActivePowerUp>(activePowerUps); // Copiar para iterar de forma segura.
         foreach(var powerup in powerupsToStop) {
             if (powerup.ExpiryCoroutine != null) StopCoroutine(powerup.ExpiryCoroutine);
             if (powerup.VisualEffect != null) powerup.VisualEffect.SetActive(false);
         }
         activePowerUps.Clear(); // Limpiar lista de seguimiento.
     }

    // --- SECCIÓN: Lógica de Mecánica Nueva (Dash Server) ---
    // Corutina en servidor para ejecutar el movimiento del dash.
    private IEnumerator PerformDashServer(Vector3 direction) {
         if (!IsServer) yield break; // Solo servidor.
         isDashing = true; // Marcar inicio de dash.
         bool originalGravity = false; // Guardar estado original de gravedad.
         if(rb!= null) { originalGravity = rb.useGravity; rb.useGravity = false; } // Desactivar gravedad.

         float elapsedTime = 0f;
         Vector3 startPos = transform.position;
         Vector3 targetPos = transform.position + direction * dashDistance; // Calcular posición objetivo.
         // Raycast para detectar colisiones y ajustar posición objetivo.
         RaycastHit hit;
         int layerMask = ~LayerMask.GetMask("Player"); // Ignorar capa del propio jugador.
         if (Physics.Raycast(startPos, direction, out hit, dashDistance, layerMask)) {
             targetPos = hit.point - direction * 0.1f; // Ajustar para no quedar dentro del obstáculo.
         }
         // Mover al jugador durante la duración del dash.
         float actualDashDistance = Vector3.Distance(startPos, targetPos);
         Vector3 velocity = (dashDuration > 0.001f) ? direction * (actualDashDistance / dashDuration) : Vector3.zero;
         while (elapsedTime < dashDuration) {
             if (rb != null && !rb.isKinematic)
             {
                 rb.MovePosition(rb.position + velocity * Time.deltaTime);
             }
             else
             {
                 transform.Translate(velocity * Time.deltaTime, Space.World);
             }
            elapsedTime += Time.deltaTime;
            yield return null; // Esperar al siguiente frame.
         }
         // Restaurar estado post-dash.
         if (rb != null) {
             rb.linearVelocity = Vector3.zero; // Detener velocidad residual.
             rb.useGravity = originalGravity; // Restaurar gravedad.
         }
         isDashing = false; // Marcar fin de dash.
    }
}
