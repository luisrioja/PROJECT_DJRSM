using UnityEngine;
using Unity.Netcode;
using TMPro;
using UnityEngine.UI;
using Unity.Netcode.Components;
using System.Collections;
using System.Collections.Generic;

// =============================================================================
// --- ENUMERACIONES GLOBALES ---
// =============================================================================

// Define los tipos de Power-Up disponibles en el juego.
// Se mueve fuera de la clase Player para un acceso más fácil desde otros scripts.
public enum PowerUpType { Speed, FireRate, Health }

// =============================================================================
// --- CLASE PRINCIPAL DEL JUGADOR ---
// =============================================================================

// Componentes requeridos para asegurar la funcionalidad básica y de red.
[RequireComponent(typeof(NetworkObject))]     // Esencial para la identificación en red.
[RequireComponent(typeof(NetworkTransform))]  // Para sincronizar posición/rotación.
[RequireComponent(typeof(Rigidbody))]         // Para físicas (colisiones, movimiento basado en físicas si se usa).
public class Player : NetworkBehaviour
{
    // ==================================
    // --- SECCIÓN: Variables Miembro (Configurables en Inspector) ---
    // ==================================

    // ----- Referencias a otros Objetos/Prefabs -----
    [Header("Referencias (Asignar en Inspector)")]
    [Tooltip("Prefab de la barra de vida flotante (UI World Space)")]
    [SerializeField] private GameObject healthBarPrefab;
    [Tooltip("Transform hijo que indica dónde posicionar la barra de vida flotante")]
    [SerializeField] private Transform healthBarAnchor;
    [Tooltip("MeshRenderer principal del jugador para cambiar su color")]
    [SerializeField] private MeshRenderer playerMeshRenderer;
    [Tooltip("Transform hijo que indica dónde se originan los disparos")]
    [SerializeField] private Transform firePoint;
    [Tooltip("Prefab de la bala (Debe tener Bullet.cs, NetworkObject, Rigidbody, etc.)")]
    [SerializeField] private GameObject bulletPrefab;
    [Tooltip("Prefab del efecto visual al impactar la bala (Cliente)")]
    [SerializeField] private GameObject bulletImpactEffectPrefab;
    [Tooltip("Prefab del efecto visual al morir el jugador (Servidor-Cliente)")]
    [SerializeField] private GameObject deathEffectPrefab;
    [Tooltip("Prefab del efecto visual al realizar el dash (Cliente)")]
    [SerializeField] private GameObject dashEffectPrefab;

    // ----- Cámara Primera Persona -----
    [Header("Cámara Primera Persona")]
    [Tooltip("Componente Camera hijo del jugador")]
    [SerializeField] private Camera playerCamera;
    [Tooltip("Componente AudioListener asociado a la cámara del jugador")]
    [SerializeField] private AudioListener audioListener;
    [Tooltip("Script PlayerLook para control de cámara (AHORA gestionado por servidor)")]
    [SerializeField] private PlayerLook playerLookScript; // Referencia necesaria para aplicar rotación vertical local

    // ----- Movimiento -----
    [Header("Movimiento")]
    [Tooltip("Velocidad base de movimiento del jugador")]
    [SerializeField] private float moveSpeed = 5.0f;

    // ----- Rotación Cámara (Autoridad Servidor) ----- 
    [Header("Rotación Cámara (Autoridad Servidor)")]
    [Tooltip("Sensibilidad del ratón aplicada en el servidor")]
    [SerializeField] private float serverMouseSensitivity = 100f;
    [Tooltip("Ángulo vertical mínimo (mirar abajo)")]
    [SerializeField] private float minVerticalAngle = -90f;
    [Tooltip("Ángulo vertical máximo (mirar arriba)")]
    [SerializeField] private float maxVerticalAngle = 90f;

    // ----- Vida -----
    [Header("Vida")]
    [Tooltip("Salud máxima del jugador")]
    [SerializeField] private int maxHealth = 100;
    [Tooltip("Color que adopta el jugador al morir")]
    [SerializeField] private Color deadColor = Color.gray;

    // ----- Disparo -----
    [Header("Disparo")]
    [Tooltip("Tiempo mínimo en segundos entre disparos (Cadencia)")]
    [SerializeField] private float fireRate = 0.5f;
    private float nextFireTime = 0f; // Control interno de cooldown

    // ----- Interacción (Puertas) -----
    [Header("Interacción (Puertas)")]
    [Tooltip("Radio en el que el jugador puede interactuar con objetos")]
    [SerializeField] private float interactionRadius = 2.0f;
    [Tooltip("Capa física asignada a los objetos interactuables (Puertas)")]
    [SerializeField] private LayerMask interactableLayer;

    // ----- Power-Ups -----
    [Header("Power-Ups")]
    [Tooltip("Duración estándar en segundos de los efectos de power-up")]
    [SerializeField] private float powerUpDuration = 10.0f;
    [Tooltip("Multiplicador aplicado a la velocidad con el power-up")]
    [SerializeField] private float speedBoostMultiplier = 1.5f;
    [Tooltip("Multiplicador para la cadencia de disparo (mayor valor = más rápido)")]
    [SerializeField] private float fireRateBoostMultiplier = 2f; // Se divide fireRate base por este valor
    [Tooltip("Cantidad de vida recuperada por el power-up de salud")]
    [SerializeField] private int healthBoostAmount = 50;
    [Tooltip("Efecto visual local a activar durante el Speed Boost")]
    [SerializeField] private GameObject speedEffectVisual;
    [Tooltip("Efecto visual local a activar durante el Fire Rate Boost")]
    [SerializeField] private GameObject fireRateEffectVisual;
    private List<ActivePowerUp> activePowerUps = new List<ActivePowerUp>(); // Seguimiento local de efectos activos

    // ----- Mecánica Nueva (Dash) -----
    [Header("Mecánica Nueva (Dash)")]
    [Tooltip("Distancia recorrida durante el dash")]
    [SerializeField] private float dashDistance = 5f;
    [Tooltip("Tiempo mínimo en segundos entre dashes (Cooldown)")]
    [SerializeField] private float dashCooldown = 2f;
    [Tooltip("Duración del movimiento rápido/invulnerabilidad del dash")]
    [SerializeField] private float dashDuration = 0.2f;
    private float nextDashTime = 0f; // Control interno de cooldown
    private bool isDashing = false; // Estado para evitar acciones durante el dash

    // ==================================
    // --- SECCIÓN: Network Variables ---
    // ==================================
    // Variables sincronizadas automáticamente por Netcode.

    [Tooltip("Color visual actual del jugador (asignado por GameManager, puede cambiar al morir)")]
    public NetworkVariable<Color> NetworkColor = new NetworkVariable<Color>(
        Color.white, // Valor inicial por defecto
        NetworkVariableReadPermission.Everyone, // Todos pueden leer
        NetworkVariableWritePermission.Server); // Solo el servidor escribe

    [Tooltip("Salud actual del jugador (0 a maxHealth)")]
    public NetworkVariable<int> NetworkSalud = new NetworkVariable<int>(
        100, // Valor inicial
        NetworkVariableReadPermission.Everyone, // Todos pueden leer
        NetworkVariableWritePermission.Server); // Solo el servidor escribe

    [Tooltip("Indica si el jugador está muerto (vida <= 0)")]
    public NetworkVariable<bool> IsDead = new NetworkVariable<bool>(
        false, // Valor inicial
        NetworkVariableReadPermission.Everyone, // Todos pueden leer
        NetworkVariableWritePermission.Server); // Solo el servidor escribe

    // --- NUEVA NetworkVariable para Rotación Vertical ---
    [Tooltip("Rotación vertical actual de la cámara (Pitch), gestionada por el servidor.")]
    private NetworkVariable<float> networkVerticalRotation = new NetworkVariable<float>(
        0f,                                     // Valor inicial
        NetworkVariableReadPermission.Everyone, // Todos pueden leer
        NetworkVariableWritePermission.Server   // Solo el servidor puede escribir
    );
    // --- FIN NetworkVariable ---

    // ==================================
    // --- SECCIÓN: Referencias Internas y Estado Local ---
    // ==================================

    // Referencias a componentes UI cacheadas.
    private TMP_Text textoVida;           // Texto para la vida en la UI local (Screen Space).
    private Slider healthBarSlider;       // Slider de la barra de vida flotante (World Space).
    private GameObject healthBarInstance; // Instancia del prefab de la barra flotante.

    // Componentes físicos cacheados.
    private Rigidbody rb;

    // Valores base para restaurar después de efectos temporales (Power-ups).
    private float baseMoveSpeed;
    private float baseFireRate;

    // --- Propiedades Públicas (Getters) ---
    // Permiten a otros scripts leer valores privados/serializados de forma segura.
    public int MaxHealth => maxHealth;
    public int HealthBoostAmount => healthBoostAmount;
    // public float PowerUpDuration => powerUpDuration; // Opcional si se necesita externamente

    // Estructura interna para gestionar la duración y efectos visuales locales de los power-ups.
    private struct ActivePowerUp {
        public PowerUpType Type;
        public Coroutine ExpiryCoroutine; // Corutina que revierte el efecto visual local.
        public GameObject VisualEffect;   // Referencia al GameObject del efecto visual activado.
    }

    // =========================================
    // --- SECCIÓN: Métodos de Ciclo de Vida (Monobehaviour / NetworkBehaviour) ---
    // =========================================

    
    /// Se llama cuando el NetworkObject asociado a este script es spawneado en la red.
    /// Es el punto principal para inicializaciones relacionadas con la red y específicas del propietario.
    /// </summary>
    public override void OnNetworkSpawn()
    {
        // Cachear componentes esenciales locales.
        rb = GetComponent<Rigidbody>();
        if (rb == null) Debug.LogError($"Player {OwnerClientId}: Necesita un Rigidbody.");
        // playerMeshRenderer es opcional pero recomendado para feedback visual.
        if (playerMeshRenderer == null) Debug.LogWarning($"Player {OwnerClientId}: Asignar 'Player Mesh Renderer' en Inspector para cambios de color.");
        // Obtener referencia a PlayerLook (necesaria para callbacks de rotación).
        playerLookScript = GetComponent<PlayerLook>();
        if (playerLookScript == null) Debug.LogError($"Player {OwnerClientId}: PlayerLook script no encontrado/asignado. La rotación de cámara NO funcionará.");


        // Guardar valores base para poder revertir efectos de Power-ups.
        baseMoveSpeed = moveSpeed;
        baseFireRate = fireRate;

        // Aplicar estado visual inicial basado en las NetworkVariables (importante si se une a mitad de partida).
        OnIsDeadChanged(false, IsDead.Value); // Aplica estado muerto/vivo inicial.
        ColorChanged(NetworkColor.Value, NetworkColor.Value); // Aplica color inicial.

        // Suscribirse a los eventos de cambio de las NetworkVariables para reaccionar visualmente.
        NetworkColor.OnValueChanged += ColorChanged;
        NetworkSalud.OnValueChanged += OnHealthChanged;
        IsDead.OnValueChanged += OnIsDeadChanged;

        // --- SUSCRIPCIÓN A NUEVA NETWORKVARIABLE ---
        // Solo los clientes no-servidores necesitan suscribirse al cambio de rotación vertical.
        // El host (servidor+cliente) lo aplicará directamente en el RPC.
        if (!IsServer)
        {
            networkVerticalRotation.OnValueChanged += HandleVerticalRotationChanged;
        }
        // Aplicar rotación vertical inicial al spawnear (para todos, incluyendo late joiners)
        if (playerLookScript != null)
        {
            playerLookScript.SetVerticalRotationLocally(networkVerticalRotation.Value);
        }
        // --- FIN SUSCRIPCIÓN ---


        // --- Lógica Específica del Propietario vs. Remoto ---
        if (IsOwner) // Este script pertenece al jugador local.
        {
            // --- ACTIVACIÓN DE CÁMARA Y CONTROL LOCAL ---
            // Habilitar la cámara y el listener de este jugador.
            if (playerCamera != null) playerCamera.enabled = true;
            else Debug.LogError($"Player {OwnerClientId}: Player Camera no asignada en Inspector.");
            if (audioListener != null) audioListener.enabled = true;
            else Debug.LogError($"Player {OwnerClientId}: Audio Listener no asignado en Inspector.");

            // IMPORTANTE: El script PlayerLook YA NO se activa/desactiva aquí.
            // Debe estar SIEMPRE ACTIVO en el prefab para leer input (si es dueño) y aplicar rotación (siempre).
            // if (playerLookScript != null) playerLookScript.enabled = true; // <-- ELIMINADO/COMENTADO

            // Desactivar la cámara principal de la escena para evitar conflictos.
             Camera sceneCamera = Camera.main;
             if (sceneCamera != null && sceneCamera != playerCamera)
             {
                 sceneCamera.gameObject.SetActive(false);
                 Debug.Log($"Player {OwnerClientId}: Desactivando cámara principal de la escena.");
             }
             // --- FIN ACTIVACIÓN CÁMARA ---

            // Configurar la interfaz de usuario local (texto de vida).
            SetupLocalUI();

            // NOTA: La solicitud de datos iniciales (color/spawn) ahora la gestiona GameManager al conectar.
        }
        else // Este script pertenece a un jugador remoto.
        {
             // --- DESACTIVACIÓN DE CÁMARA Y CONTROL REMOTO ---
             // Deshabilitar cámara y listener para no ver/oir desde este avatar.
            if (playerCamera != null) playerCamera.enabled = false;
            if (audioListener != null) audioListener.enabled = false;
            // PlayerLook tampoco se desactiva aquí, necesita aplicar la rotación vertical recibida.
            // if (playerLookScript != null) playerLookScript.enabled = false; // <-- ELIMINADO/COMENTADO
            // --- FIN DESACTIVACIÓN CÁMARA ---

            // Configurar la barra de vida flotante sobre este avatar remoto.
            SetupFloatingHealthBar();
        }

        // Verificar configuración de NetworkTransform (debe ser Server Authoritative según PDF).
        VerifyNetworkTransformConfig();

        // Ajustar físicas: Los clientes no deben simular física principal para evitar desincronización.
        // NetworkRigidbody/Transform gestionan la sincronización desde el servidor.
        // Si usamos NetworkRigidbody, él maneja el isKinematic. Si usamos NetworkTransform con Rigidbody, lo hacemos manualmente.
        var netRigidbody = GetComponent<NetworkRigidbody>(); // Comprobar si existe NetworkRigidbody
        if (!IsServer && netRigidbody == null) // Solo hacerlo si NO usamos NetworkRigidbody
        {
             rb.isKinematic = true;
        }
        // Si IS DEAD, isKinematic se gestiona en OnIsDeadChanged
        if (IsDead.Value) rb.isKinematic = true;
    }

    
    /// Se llama cada frame. Usado principalmente para leer input local (si IsOwner).
    /// </summary>
    void Update()
    {
        // Ignorar si no somos el propietario, estamos muertos o realizando un dash.
        if (!IsOwner || IsDead.Value || isDashing) return;

        // --- Procesamiento de Input Local ---

        // --- Movimiento ---
        float h = Input.GetAxis("Horizontal"); // Input A/D o Izquierda/Derecha.
        float v = Input.GetAxis("Vertical");   // Input W/S o Arriba/Abajo.
        Vector3 moveDir = new Vector3(h, 0, v); // Dirección relativa local.
        // Si hay input de movimiento, normalizar y enviar petición al servidor.
        if (moveDir != Vector3.zero) SubmitMovementRequestServerRpc(moveDir.normalized);

        // --- Disparo ---
        // Usar GetButton para permitir mantener presionado (disparo automático si fireRate lo permite).
        if (Input.GetButton("Fire1") && Time.time >= nextFireTime)
        {
            nextFireTime = Time.time + fireRate; // Aplicar cooldown localmente (prevención de spam).
            if (firePoint != null) {
                // Enviar petición de disparo al servidor con la posición/rotación del punto de disparo.
                SubmitFireRequestServerRpc(firePoint.position, firePoint.rotation);
            } else { Debug.LogError($"Player {OwnerClientId}: Fire Point no está asignado."); }
        }

        // --- Interacción ---
        // Detectar pulsación de la tecla E.
        if (Input.GetKeyDown(KeyCode.E))
        {
            // Enviar petición de interacción al servidor.
            SubmitInteractionRequestServerRpc();
        }

        // --- Dash ---
        // Detectar pulsación de la tecla Shift Izquierdo y verificar cooldown.
        if (Input.GetKeyDown(KeyCode.LeftShift) && Time.time >= nextDashTime)
        {
             Vector3 dashDir = moveDir.normalized; // Usar dirección de movimiento actual si existe.
             if (dashDir == Vector3.zero) dashDir = transform.forward; // Si no, usar dirección hacia donde mira.
             nextDashTime = Time.time + dashCooldown; // Aplicar cooldown.
             // Enviar petición de dash al servidor.
             SubmitDashRequestServerRpc(dashDir);
        }

        // --- INPUT DE CÁMARA ---
        // PlayerLook.cs ahora lee el input del ratón en su propio Update()
        // y llama a UpdateLookInputServerRpc() directamente desde allí.
        // Ya no necesitamos leer Input.GetAxis("Mouse X/Y") aquí.
    }

    
    /// Se llama cuando el NetworkObject es despawneado o destruido.
    /// Importante para desuscribirse de eventos y limpiar referencias.
    /// </summary>
    public override void OnNetworkDespawn()
    {
        // Desuscribirse de eventos de NetworkVariables para evitar memory leaks o errores.
        if (NetworkColor != null) NetworkColor.OnValueChanged -= ColorChanged;
        if (NetworkSalud != null) NetworkSalud.OnValueChanged -= OnHealthChanged;
        if (IsDead != null) IsDead.OnValueChanged -= OnIsDeadChanged;

        // --- DESUSCRIPCIÓN DE NUEVA NETWORKVARIABLE ---
        if (networkVerticalRotation != null && !IsServer) // Solo desuscribir si estábamos suscritos
        {
            networkVerticalRotation.OnValueChanged -= HandleVerticalRotationChanged;
        }
        // --- FIN DESUSCRIPCIÓN ---

        // Limpiar instancias de UI creadas dinámicamente.
        if (healthBarInstance != null) Destroy(healthBarInstance);

        // Detener corutinas locales (importante para power-ups) y limpiar efectos visuales.
        StopAllPowerupCoroutinesAndVisuals();

        // Llamar a la implementación base.
        base.OnNetworkDespawn();
    }

    // =================================================
    // --- SECCIÓN: Configuración Inicial y UI Local ---
    // =================================================

    
    /// Configura la interfaz de usuario específica para el jugador local (ej. texto de vida).
    /// </summary>
    private void SetupLocalUI()
    {
        var vidaPlayerObject = GameObject.Find("VidaPlayer"); // Busca por nombre, requiere objeto en escena.
        if (vidaPlayerObject != null) {
            textoVida = vidaPlayerObject.GetComponent<TMP_Text>();
            if (textoVida != null) UpdateLocalHealthUI(NetworkSalud.Value); // Mostrar vida inicial.
            else Debug.LogError($"Player {OwnerClientId}: No se encontró TMP_Text en 'VidaPlayer'");
        } else Debug.LogError($"Player {OwnerClientId}: No se encontró 'VidaPlayer' en la escena.");
    }

    
    /// Configura la barra de vida flotante para avatares remotos.
    /// </summary>
    private void SetupFloatingHealthBar()
    {
        if (healthBarPrefab != null && healthBarAnchor != null) {
            healthBarInstance = Instantiate(healthBarPrefab, healthBarAnchor.position, healthBarAnchor.rotation);
            healthBarInstance.transform.SetParent(healthBarAnchor, true); // Emparentar al anchor.
            healthBarSlider = healthBarInstance.GetComponentInChildren<Slider>(); // Buscar Slider en hijos.
            if (healthBarSlider != null) UpdateFloatingHealthBar(NetworkSalud.Value); // Mostrar vida inicial.
            else Debug.LogError($"Player {OwnerClientId}: Prefab de barra de vida no tiene Slider.");
        } else Debug.LogError($"Player {OwnerClientId}: Falta healthBarPrefab o healthBarAnchor en Inspector.");
    }

     
     /// Verifica si NetworkTransform está configurado como Server Authoritative.
     /// </summary>
     private void VerifyNetworkTransformConfig() {
        var networkTransform = GetComponent<NetworkTransform>();
        if (networkTransform == null) Debug.LogError($"Player {OwnerClientId}: NetworkTransform no encontrado.");
        // IsServerAuthoritative() es el chequeo correcto para versiones con y sin la opción en Inspector.
        // En Netcode for GameObjects >= 1.0, NetworkTransform es inherentemente Server Authoritative.
        // ClientNetworkTransform existe para autoridad del cliente.
        // Así que esta comprobación puede ser menos relevante o necesitar adaptarse a la versión exacta.
        // else if (!networkTransform.IsServerAuthoritative()) // Podría dar error si el método no existe
        //      Debug.LogWarning($"Player {OwnerClientId}: ¡CONFIGURACIÓN INCORRECTA! NetworkTransform NO es Server Authoritative. El PDF requiere autoridad del servidor.");
     }

    // ===============================================
    // --- SECCIÓN: Callbacks de Network Variables ---
    // ===============================================
    // Métodos llamados automáticamente en TODOS los clientes cuando el valor de una NetworkVariable cambia en el servidor.

    
    /// Callback para cambios en NetworkSalud. Actualiza la UI correspondiente.
    /// </summary>
    private void OnHealthChanged(int previousValue, int newValue)
    {
        // Actualiza UI local si somos el dueño, o la barra flotante si es un jugador remoto.
        if (IsOwner) UpdateLocalHealthUI(newValue);
        else UpdateFloatingHealthBar(newValue);
    }

    
    /// Callback para cambios en NetworkColor. Actualiza el color visual del avatar.
    /// </summary>
    private void ColorChanged(Color previousValue, Color newValue)
    {
        if (playerMeshRenderer != null)
        {
            // El color de muerto tiene prioridad sobre el color normal asignado.
            playerMeshRenderer.material.color = IsDead.Value ? deadColor : newValue;
        }
    }

    
    /// Callback para cambios en IsDead. Gestiona el estado visual y físico de muerte/respawn.
    /// </summary>
    private void OnIsDeadChanged(bool previousValue, bool newValue)
    {
        bool justDied = newValue && !previousValue;    // Transición de vivo a muerto.
        bool justRespawned = !newValue && previousValue; // Transición de muerto a vivo.

        // --- Actualización Visual ---
        if (playerMeshRenderer != null)
        {
            // Aplicar color correspondiente (gris si muerto, NetworkColor si vivo).
            playerMeshRenderer.material.color = newValue ? deadColor : NetworkColor.Value;
            // Opcional: Podríamos ocultar el renderer para otros clientes si está muerto.
            playerMeshRenderer.enabled = !newValue || IsOwner; // Mostrar siempre si somos dueños (para verlo gris) o si está vivo.
        }
        // Ocultar/Mostrar barra de vida flotante.
        if (healthBarInstance != null) healthBarInstance.SetActive(!newValue);

        // --- Actualización Física y de Estado ---
        // No desactivar el collider principal para evitar caer por el suelo.
        // GetComponent<Collider>().enabled = !newValue; // COMENTADO/ELIMINADO

        // Hacer el Rigidbody kinemático al morir para detener física y evitar que otros lo muevan.
        if (rb != null) {
            bool shouldBeKinematic = newValue || (!IsServer && GetComponent<NetworkRigidbody>() == null); // Muerto O cliente sin NetworkRigidbody
            if (rb.isKinematic != shouldBeKinematic) // Solo cambiar si es necesario
            {
                 rb.isKinematic = shouldBeKinematic;
            }

            if (justDied) { // Si acaba de morir.
                rb.linearVelocity = Vector3.zero;        // Detener todo movimiento físico.
                rb.angularVelocity = Vector3.zero; // Detener toda rotación física.
            }
        }

        // --- Lógica Adicional al Morir/Revivir ---
        if (justDied) {
            // En el servidor: Instanciar efecto de muerte si existe.
            if (IsServer && deathEffectPrefab != null) {
                GameObject deathFx = Instantiate(deathEffectPrefab, transform.position, Quaternion.identity);
                NetworkObject deathFxNetObj = deathFx.GetComponent<NetworkObject>();
                 // Si el efecto es un NetworkObject, spawnearlo; si no, destruirlo tras un tiempo.
                 if (deathFxNetObj != null) deathFxNetObj.Spawn(true);
                 else Destroy(deathFx, 3f);
            }
            // En el cliente propietario: Mostrar mensaje, detener efectos locales.
            if (IsOwner) {
                 Debug.Log("¡Has Muerto!"); // Mensaje simple, podría ser UI.
                 StopAllPowerupCoroutinesAndVisuals(); // Limpiar efectos de power-ups.
            }
             // Respawn automático desactivado para cumplir requisito de "ver muerto".
             // Se necesita un mecanismo externo (GameManager, input) para llamar a Respawn().
             // // StartCoroutine(RespawnTimer(5.0f));
        }

        // Si acaba de revivir, se podrían restaurar otros estados si fuera necesario.
        if (justRespawned) {
            if (IsOwner) {
                 Debug.Log("¡Has Revivido!");
            }
        }
    }

    // --- NUEVO CALLBACK para Rotación Vertical ---
    
    /// Callback para cambios en networkVerticalRotation. Llamado en clientes no-servidores.
    /// </summary>
    private void HandleVerticalRotationChanged(float previousValue, float newValue)
    {
        // Llama al método en PlayerLook para aplicar la rotación al cameraHolder local.
        if (playerLookScript != null)
        {
            playerLookScript.SetVerticalRotationLocally(newValue);
        }
    }
    // --- FIN NUEVO CALLBACK ---

    // ===========================================
    // --- SECCIÓN: Actualización de UI / Visual ---
    // ===========================================

    
    /// Actualiza el texto de la UI local con la vida actual.
    /// </summary>
    private void UpdateLocalHealthUI(int currentHealth) {
        if (textoVida != null) textoVida.text = "HEALTH: " + currentHealth.ToString();
    }

    
    /// Actualiza el valor del slider de la barra de vida flotante (como porcentaje).
    /// </summary>
    private void UpdateFloatingHealthBar(int currentHealth) {
        if (healthBarSlider != null) healthBarSlider.value = (float)currentHealth / maxHealth; // Calcula porcentaje.
        // Asegurar que la barra esté visible solo si el jugador está vivo.
        if (healthBarInstance != null) healthBarInstance.SetActive(currentHealth > 0 && !IsDead.Value);
    }

    // ============================================
    // --- SECCIÓN: Server RPCs (Cliente -> Servidor) ---
    // ============================================
    // Métodos marcados con [ServerRpc] que un cliente propietario puede llamar,
    // pero que se ejecutan exclusivamente en el servidor.

    
    /// [ServerRpc] Opcional. Solicitud inicial de datos desde el cliente.
    /// La asignación de color/spawn la maneja GameManager al conectar.
    /// </summary>
    [ServerRpc]
    private void RequestInitialDataServerRpc(ServerRpcParams rpcParams = default)
    {
        // Este RPC puede quedar vacío si GameManager maneja toda la inicialización.
        ulong clientId = rpcParams.Receive.SenderClientId;
        Debug.Log($"Servidor: Recibida llamada (vacía) RequestInitialDataServerRpc de {clientId}.");
    }

    
    /// [ServerRpc] Recibe la dirección de movimiento normalizada desde el cliente y mueve el avatar en el servidor.
    /// </summary>
    [ServerRpc]
    void SubmitMovementRequestServerRpc(Vector3 direction, ServerRpcParams rpcParams = default)
    {
        // Ignorar si el jugador que envió el RPC está muerto o haciendo dash.
        if (IsDead.Value || isDashing) return;
        // Aplicar movimiento al transform. NetworkTransform sincronizará la posición.
        // Usar rb.MovePosition si se prefiere movimiento basado en física y se gestionan colisiones mejor.
        // transform.Translate(direction * moveSpeed * Time.deltaTime, Space.World);
        // Alternativa con Rigidbody (mejor para colisiones si no es kinematic):
        if (rb != null && !rb.isKinematic)
        {
            rb.MovePosition(rb.position + transform.TransformDirection(direction) * moveSpeed * Time.deltaTime);
        }
        else // Fallback a Translate si es kinematic o no hay rb
        {
             transform.Translate(transform.TransformDirection(direction) * moveSpeed * Time.deltaTime, Space.World);
        }
    }

    // --- NUEVO ServerRpc para Input de Cámara ---
    
    /// [ServerRpc] Recibe el delta del input del ratón desde el cliente propietario.
    /// Ejecuta la lógica de rotación en el servidor.
    /// </summary>
    [ServerRpc]
    public void UpdateLookInputServerRpc(float mouseXDelta, float mouseYDelta, ServerRpcParams rpcParams = default)
    {
        // Solo el servidor ejecuta esto
        ulong clientId = rpcParams.Receive.SenderClientId; // Podría usarse para validación si fuera necesario

        // Ignorar si el jugador está muerto o haciendo dash
        if (IsDead.Value || isDashing) return;

        // 1. Aplicar rotación horizontal al Player (el NetworkTransform la sincronizará)
        // Usamos Rotate para aplicar el delta directamente. Aplicamos sensibilidad y delta time aquí.
        transform.Rotate(Vector3.up * mouseXDelta * serverMouseSensitivity * Time.deltaTime);

        // 2. Calcular y aplicar rotación vertical (guardarla en NetworkVariable)
        float currentVertical = networkVerticalRotation.Value;
        // Aplicamos sensibilidad, delta time y restamos Y porque Input.GetAxis("Mouse Y") es positivo hacia arriba.
        currentVertical -= mouseYDelta * serverMouseSensitivity * Time.deltaTime;
        // Limitar (Clamp) la rotación vertical.
        currentVertical = Mathf.Clamp(currentVertical, minVerticalAngle, maxVerticalAngle);

        // Actualizar la NetworkVariable (esto notificará a los clientes suscritos mediante HandleVerticalRotationChanged)
        networkVerticalRotation.Value = currentVertical;

        // Opcional: Si el servidor también es un cliente (Host), aplicar directamente la rotación vertical localmente
        // para evitar la pequeña latencia de espera al callback de la NetworkVariable.
         if (IsHost && playerLookScript != null)
         {
             playerLookScript.SetVerticalRotationLocally(currentVertical);
         }
    }
    // --- FIN NUEVO ServerRpc ---

    
    /// [ServerRpc] Recibe la petición de disparo, instancia y hace spawn de la bala en el servidor.
    /// </summary>
    [ServerRpc]
    void SubmitFireRequestServerRpc(Vector3 spawnPos, Quaternion spawnRot, ServerRpcParams rpcParams = default)
    {
        // Ignorar si muerto o haciendo dash.
        if (IsDead.Value || isDashing) return;
        ulong clientId = rpcParams.Receive.SenderClientId;

        // Validar cooldown en servidor si es necesario para mayor seguridad.

        // Comprobar si el prefab de bala está asignado.
        if (bulletPrefab != null)
        {
            // Instanciar el prefab en la posición/rotación del firePoint.
            GameObject bulletInstance = Instantiate(bulletPrefab, spawnPos, spawnRot);
            // Obtener componentes necesarios de la instancia.
            NetworkObject bulletNetworkObject = bulletInstance.GetComponent<NetworkObject>();
            Bullet bulletScript = bulletInstance.GetComponent<Bullet>();

            // Si la bala está correctamente configurada (tiene NetworkObject y Bullet script).
            if (bulletNetworkObject != null && bulletScript != null)
            {
                // Obtener el color visual actual del jugador (puede diferir de NetworkColor si está muerto).
                Color jugadorColorActual = playerMeshRenderer != null ? playerMeshRenderer.material.color : NetworkColor.Value;
                // Si está muerto, usamos el color de muerto para la bala (aunque no debería poder disparar muerto).
                //if (IsDead.Value) jugadorColorActual = deadColor;

                // Inicializar la bala con datos del dueño, color y efecto de impacto. (ANTES de Spawn).
                bulletScript.Initialize(OwnerClientId, jugadorColorActual, bulletImpactEffectPrefab);

                // Hacer que la bala aparezca en la red para todos los clientes. (El servidor es el propietario).
                bulletNetworkObject.Spawn(true); // true = destruir con el servidor.
                // Debug.Log($"Servidor: Bala spawneada para {clientId}");
            }
            else // Error en la configuración del prefab de bala.
            {
                 Debug.LogError($"Player {clientId}: Prefab de bala ({bulletPrefab.name}) no tiene NetworkObject o Bullet script.");
                 Destroy(bulletInstance); // Destruir instancia local fallida.
            }
        }
        else // Error: Prefab de bala no asignado en el Inspector.
        {
             Debug.LogError($"Player {clientId}: Prefab de bala no asignado en el Inspector.");
        }
    }

     
    /// [ServerRpc] Recibe petición de interacción (tecla E). Busca puertas cercanas y las activa.
    /// </summary>
    [ServerRpc]
    void SubmitInteractionRequestServerRpc(ServerRpcParams rpcParams = default)
    {
        // Ignorar si muerto o haciendo dash.
        if (IsDead.Value || isDashing) return;
        ulong clientId = rpcParams.Receive.SenderClientId;
        // Debug.Log($"Servidor: Jugador {clientId} intentó interactuar.");

        // Buscar colliders cercanos en la capa 'Interactable'.
        Collider[] hits = Physics.OverlapSphere(transform.position, interactionRadius, interactableLayer);
        GameObject closestInteractable = null;
        float minDistance = float.MaxValue;

        // Iterar sobre los colliders encontrados.
        foreach (var hitCollider in hits)
        {
            // Comprobar si el objeto tiene el script 'Door'.
            Door door = hitCollider.GetComponent<Door>();
            if (door != null)
            {
                // Versión simple: Si está en el radio y capa, es interactuable.
                float distance = Vector3.Distance(transform.position, hitCollider.transform.position);
                if (distance < minDistance) {
                    minDistance = distance;
                    closestInteractable = hitCollider.gameObject;
                }
                // Opcional: Añadir Raycast para comprobar línea de visión.
                /* ... (código de Raycast omitido por simplicidad, añadir si es necesario) ... */
            }
        }

        // Si se encontró un objeto interactuable cercano.
        if (closestInteractable != null)
        {
            // Debug.Log($"Servidor: Jugador {clientId} interactuando con {closestInteractable.name}");
            Door doorToToggle = closestInteractable.GetComponent<Door>();
            if (doorToToggle != null) {
                // Llamar al método público en Door.cs para cambiar su estado.
                doorToToggle.ToggleDoorState();
            }
        } // else { Debug.Log($"Servidor: Jugador {clientId} no encontró nada con qué interactuar."); }
    }

     
    /// [ServerRpc] Recibe petición de dash, ejecuta la corutina de movimiento y notifica a clientes.
    /// </summary>
    [ServerRpc]
    void SubmitDashRequestServerRpc(Vector3 direction)
    {
        // Ignorar si muerto o ya haciendo dash.
        if (IsDead.Value || isDashing) return;
        // Iniciar la corutina que realiza el movimiento rápido en el servidor.
        StartCoroutine(PerformDashServer(direction));
        // Enviar RPC a todos los clientes para mostrar el efecto visual.
        NotifyDashClientRpc(direction);
    }


    // ===========================================
    // --- SECCIÓN: Client RPCs (Servidor -> Cliente(s)) ---
    // ===========================================
    // Métodos marcados con [ClientRpc] que el servidor puede llamar
    // para que se ejecuten en uno o varios clientes específicos.

     
    /// [ClientRpc] Teletransporta forzosamente al cliente propietario a una posición. Útil para spawn inicial.
    /// </summary>
    [ClientRpc]
    private void TeleportClientRpc(Vector3 position, ClientRpcParams rpcParams = default)
    {
        // El RPC se envía a un cliente específico, pero por seguridad comprobamos IsOwner.
        if (!IsOwner) return;
        // Es más seguro mover el Rigidbody si existe y no es kinemático.
        if (rb != null && !rb.isKinematic) rb.position = position;
        else transform.position = position; // Fallback a transform.position
    }

     
    /// [ClientRpc] Muestra el efecto visual del dash en todos los clientes.
    /// </summary>
    [ClientRpc]
    void NotifyDashClientRpc(Vector3 direction)
    {
        // Instanciar efecto visual si está asignado y autodestruirlo.
        if (dashEffectPrefab != null) {
             GameObject fx = Instantiate(dashEffectPrefab, transform.position, Quaternion.LookRotation(direction));
             Destroy(fx, 1.0f); // Destruir efecto después de 1 segundo.
        }
    }

     
    /// [ClientRpc] Notifica al cliente propietario que ha recogido un power-up.
    /// Inicia la lógica local para feedback visual y de UI.
    /// </summary>
    [ClientRpc]
    private void NotifyPowerUpPickupClientRpc(PowerUpType type, ClientRpcParams clientRpcParams = default)
    {
        // El RPC ya está dirigido al cliente correcto (ver ServerHandlePowerUpPickup).
        // Comprobar IsOwner es una buena práctica por si acaso.
        if (!IsOwner) return;

        // Debug.Log($"Player (Cliente {OwnerClientId}): Recibí notificación de recogida de {type}!");
        // Iniciar la corutina local que maneja los efectos visuales y su duración.
        StartCoroutine(HandlePowerUpEffectLocal(type));
    }


    // ========================================
    // --- SECCIÓN: Lógica de Juego (Vida) ---
    // ========================================

     
    /// Aplica daño a este jugador. Solo se ejecuta si es llamado en el servidor.
    /// Ignora daño si el jugador está muerto o haciendo dash (invulnerable).
    /// </summary>
    /// <param name="amount">Cantidad de daño a aplicar.</param>
    public void TakeDamage(int amount)
    {
        // Solo el servidor puede aplicar daño, y solo si el jugador está vivo y no en dash.
        if (!IsServer || IsDead.Value || isDashing) return;

        int previousHealth = NetworkSalud.Value;
        // Restar daño y asegurar que no baje de 0.
        NetworkSalud.Value = Mathf.Max(0, NetworkSalud.Value - amount);

        // Si la vida cruzó el umbral de 0 en esta llamada, activar estado de muerte.
        if (NetworkSalud.Value <= 0 && previousHealth > 0) {
            Die();
        }
    }

     
    /// Lógica ejecutada en el servidor cuando la vida del jugador llega a 0.
    /// Marca al jugador como muerto (IsDead.Value = true).
    /// </summary>
    private void Die()
    {
        // Doble chequeo de seguridad.
        if (!IsServer || IsDead.Value) return;

        // Establecer la NetworkVariable que sincronizará el estado a los clientes
        // y disparará el callback OnIsDeadChanged.
        IsDead.Value = true;
        // Debug.Log($"Servidor: Jugador {OwnerClientId} ha muerto.");

        // NO iniciar respawn automático aquí si el requisito es ver al jugador muerto.
        // // StartCoroutine(RespawnTimer(5.0f));
    }

     
    /// Corutina de ejemplo para un temporizador de respawn (no usada actualmente).
    /// </summary>
    private IEnumerator RespawnTimer(float delay) {
         yield return new WaitForSeconds(delay);
         Respawn(); // Llamar a la lógica de respawn después del retraso.
    }

     
    /// Lógica ejecutada en el servidor para revivir al jugador.
    /// Restaura la salud, desmarca IsDead y lo reposiciona.
    /// Necesita ser llamada externamente (ej. por GameManager o input).
    /// </summary>
    public void Respawn() { // Hecho público para poder llamarlo externamente
        // Solo el servidor puede revivir.
        if (!IsServer) return;
        // Debug.Log($"Servidor: Respawneando jugador {OwnerClientId}");

        // Obtener una posición de spawn (idealmente desde GameManager).
        Vector3 spawnPos = Vector3.up; // Posición por defecto.
        GameManager gm = GameManager.Instance; // Asume Singleton GameManager.
        if (gm != null) {
             // Necesita un método en GameManager que devuelva un punto de spawn.
             spawnPos = gm.GetRandomSpawnPoint();
        }

        // Reposicionar al jugador en el servidor (NetworkTransform sincronizará).
        // Usar TeleportClientRpc para asegurar la posición exacta en el cliente propietario si fuera necesario,
        // aunque NetworkTransform debería ser suficiente si está bien configurado.
        transform.position = spawnPos;
        // Podríamos llamar a TeleportClientRpc si NetworkTransform da problemas con el respawn inmediato:
        // TeleportClientRpc(spawnPos, new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } } });


        // Restaurar salud al máximo.
        NetworkSalud.Value = maxHealth;
        // Marcar como vivo (esto dispara OnIsDeadChanged para restaurar estado visual/físico).
        IsDead.Value = false;
    }

     
    /// Método público para compatibilidad con el botón "Quitar Vida" del NetworkHUD.
    /// Redirige a TakeDamage asegurando que solo el servidor procese el daño.
    /// </summary>
    public void QuitarVida(int cantidad) {
        // Asegurarse de que solo el servidor ejecute el TakeDamage
        if (IsServer) TakeDamage(cantidad);
        // Si se llama desde cliente, podríamos necesitar un ServerRpc para pedir quitar vida.
        // else RequestTakeDamageServerRpc(cantidad); // Ejemplo si fuera necesario
    }
    // [ServerRpc] void RequestTakeDamageServerRpc(int amount) { TakeDamage(amount); }


    // ===========================================
    // --- SECCIÓN: Lógica de Power-Ups ---
    // ===========================================

     
    /// Método llamado por PowerUp.cs en el SERVIDOR cuando este jugador recoge un power-up.
    /// Centraliza la lógica del servidor y el envío del RPC al cliente propietario.
    /// </summary>
    /// <param name="type">El tipo de PowerUp recogido.</param>
    public void ServerHandlePowerUpPickup(PowerUpType type)
    {
        // Solo el servidor procesa la recogida, y solo si el jugador está vivo.
        if (!IsServer || IsDead.Value) return;

        // Aplicar el efecto lógico en el servidor (cambio de stats, inicio de corutina de duración).
        ApplyPowerUpEffectOnServer(type);

        // Enviar un ClientRpc *solo* al cliente que recogió el power-up para feedback local.
        ClientRpcParams clientRpcParams = new ClientRpcParams {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } }
        };
        NotifyPowerUpPickupClientRpc(type, clientRpcParams);
    }

     
    /// Aplica el efecto del power-up en las variables/estado del SERVIDOR.
    /// Para efectos de duración (Speed, FireRate), inicia una corutina en el servidor.
    /// </summary>
    private void ApplyPowerUpEffectOnServer(PowerUpType type)
    {
        if (!IsServer) return; // Seguridad extra.

        switch (type) {
            case PowerUpType.Speed:
                // Inicia corutina que aumenta 'moveSpeed' temporalmente en el servidor.
                StartCoroutine(ServerPowerUpDuration(type, powerUpDuration));
                break;
            case PowerUpType.FireRate:
                // Inicia corutina que disminuye 'fireRate' temporalmente en el servidor.
                StartCoroutine(ServerPowerUpDuration(type, powerUpDuration));
                break;
            case PowerUpType.Health:
                // Aplica curación instantánea, asegurando no exceder maxHealth.
                NetworkSalud.Value = Mathf.Min(maxHealth, NetworkSalud.Value + healthBoostAmount);
                break;
        }
        // Debug.Log($"Player (Servidor {OwnerClientId}): Efecto {type} aplicado.");
    }

     
    /// Corutina ejecutada en el SERVIDOR para manejar la duración de los power-ups Speed y FireRate.
    /// Modifica temporalmente las variables 'moveSpeed' o 'fireRate'.
    /// </summary>
    private IEnumerator ServerPowerUpDuration(PowerUpType type, float duration)
    {
        // Aplicar el efecto al inicio de la corutina.
        // float originalValue = 0; // Para logs o lógica más compleja.
        switch (type) {
            case PowerUpType.Speed: moveSpeed = baseMoveSpeed * speedBoostMultiplier; break;
            case PowerUpType.FireRate: fireRate = baseFireRate / fireRateBoostMultiplier; break;
        }
        // Debug.Log($"Player (Servidor {OwnerClientId}): Efecto {type} activado.");

        // Esperar la duración especificada.
        yield return new WaitForSeconds(duration);

        // Revertir el efecto usando los valores base guardados en OnNetworkSpawn.
        // ¡Importante! Asegurarse de que otro power-up del mismo tipo no haya reiniciado el timer.
        // Una gestión más robusta usaría contadores o timestamps. Esta es la versión simple.
        switch (type) {
            case PowerUpType.Speed: moveSpeed = baseMoveSpeed; break;
            case PowerUpType.FireRate: fireRate = baseFireRate; break;
        }
         // Debug.Log($"Player (Servidor {OwnerClientId}): Efecto {type} expirado.");
    }

     
    /// Corutina ejecutada en el CLIENTE PROPIETARIO para manejar los efectos VISUALES locales y su duración.
    /// </summary>
    private IEnumerator HandlePowerUpEffectLocal(PowerUpType type)
    {
        GameObject visualEffectInstance = null;
        float currentDuration = powerUpDuration; // Usar duración estándar.

        // --- Gestión de Efectos Visuales Superpuestos ---
        // Antes de activar uno nuevo, desactivar y detener el anterior del mismo tipo si existe.
        ActivePowerUp existingPowerup = activePowerUps.Find(p => p.Type == type);
        if (existingPowerup.VisualEffect != null || existingPowerup.ExpiryCoroutine != null)
        {
            if (existingPowerup.VisualEffect != null) existingPowerup.VisualEffect.SetActive(false);
            if (existingPowerup.ExpiryCoroutine != null) StopCoroutine(existingPowerup.ExpiryCoroutine);
            activePowerUps.Remove(existingPowerup);
        }
        // --- Fin Gestión Superpuestos ---


        // Activar el GameObject del efecto visual correspondiente localmente.
        switch (type) {
            case PowerUpType.Speed: if (speedEffectVisual != null) speedEffectVisual.SetActive(true); visualEffectInstance = speedEffectVisual; break;
            case PowerUpType.FireRate: if (fireRateEffectVisual != null) fireRateEffectVisual.SetActive(true); visualEffectInstance = fireRateEffectVisual; break;
            case PowerUpType.Health: currentDuration = 0; /* Efecto instantáneo, sin duración visual por defecto */ break;
        }

        // Crear registro del power-up activo localmente.
        ActivePowerUp newActivePowerUp = new ActivePowerUp { Type = type, VisualEffect = visualEffectInstance };

        // Si tiene duración o efecto visual, iniciar corutina para revertirlo visualmente.
        if (currentDuration > 0 || visualEffectInstance != null) {
            Coroutine expiry = StartCoroutine(RevertPowerUpEffectLocal(type, currentDuration, newActivePowerUp));
            newActivePowerUp.ExpiryCoroutine = expiry; // Guardar referencia a la corutina.
            activePowerUps.Add(newActivePowerUp);     // Añadir a la lista de seguimiento.
        }

        yield return null; // Necesario si no hubo yield en el switch.
    }

     
    /// Corutina ejecutada en el CLIENTE PROPIETARIO para revertir los efectos VISUALES locales después de la duración.
    /// </summary>
    private IEnumerator RevertPowerUpEffectLocal(PowerUpType type, float duration, ActivePowerUp trackingInfo)
    {
        // Esperar si hay duración.
        if (duration > 0) yield return new WaitForSeconds(duration);

        // Solo desactivar/eliminar si todavía está en la lista activa (evita errores si se limpió antes)
        if (activePowerUps.Contains(trackingInfo))
        {
             // Desactivar el efecto visual si existe.
            if (trackingInfo.VisualEffect != null) trackingInfo.VisualEffect.SetActive(false);

            // Eliminar de la lista de seguimiento local.
            activePowerUps.Remove(trackingInfo);
            // Debug.Log($"Player (Cliente {OwnerClientId}): Efecto visual local para {type} revertido.");
        }
    }

      
     /// Detiene todas las corutinas de duración de power-ups locales y desactiva sus efectos visuales.
     /// Llamado al morir o al despawnear.
     /// </summary>
     private void StopAllPowerupCoroutinesAndVisuals() {
         // Solo el propietario tiene efectos locales que gestionar.
         if (!IsOwner) return;

         // Copiar a una lista temporal para iterar porque RevertPowerUpEffectLocal modifica la lista original.
         List<ActivePowerUp> powerupsToStop = new List<ActivePowerUp>(activePowerUps);

         foreach(var powerup in powerupsToStop) {
             // Detener corutina de expiración si aún se está ejecutando.
             if (powerup.ExpiryCoroutine != null) StopCoroutine(powerup.ExpiryCoroutine);
             // Desactivar visual.
             if (powerup.VisualEffect != null) powerup.VisualEffect.SetActive(false);
         }
         // Limpiar la lista de seguimiento original.
         activePowerUps.Clear();
     }

    // =====================================================
    // --- SECCIÓN: Lógica de Mecánica Nueva (Dash Server) ---
    // =====================================================

     
    /// Corutina ejecutada en el SERVIDOR para realizar el movimiento rápido del dash.
    /// Incluye detección de colisiones simple para evitar atravesar paredes.
    /// Marca al jugador como 'isDashing' temporalmente (podría usarse para invulnerabilidad).
    /// </summary>
    private IEnumerator PerformDashServer(Vector3 direction) {
         // Solo el servidor ejecuta la lógica de movimiento real.
         if (!IsServer) yield break;

         isDashing = true; // Marcar inicio del dash. Podría usarse para evitar daño en TakeDamage.
         bool originalGravity = false;
         if(rb!= null) { originalGravity = rb.useGravity; rb.useGravity = false; } // Desactivar gravedad durante el dash.

         float elapsedTime = 0f;
         Vector3 startPos = transform.position;
         // Calcular posición objetivo inicial.
         Vector3 targetPos = transform.position + direction * dashDistance;

         // Raycast: Comprobar si hay un obstáculo en la trayectoria del dash.
         RaycastHit hit;
         // Lanzar rayo desde la posición inicial en la dirección del dash, hasta la distancia máxima.
         // Ignorar la capa del propio jugador si está configurada.
         int layerMask = ~LayerMask.GetMask("Player"); // Ejemplo: Ignorar capa "Player"
         if (Physics.Raycast(startPos, direction, out hit, dashDistance, layerMask)) {
             // Si choca, ajustar la posición objetivo para detenerse justo antes del obstáculo.
             targetPos = hit.point - direction * 0.1f; // Pequeño margen para no quedar dentro.
             // Debug.Log($"Player (Servidor {OwnerClientId}): Dash interrumpido por obstáculo {hit.collider.name}");
         }

         // Calcular velocidad constante necesaria para cubrir la distancia (ajustada o completa) en 'dashDuration'.
         float actualDashDistance = Vector3.Distance(startPos, targetPos);
         // Evitar división por cero si dashDuration es muy pequeño o cero.
         Vector3 velocity = (dashDuration > 0.001f) ? direction * (actualDashDistance / dashDuration) : Vector3.zero;

         // Mover durante 'dashDuration'.
         while (elapsedTime < dashDuration) {
            // Mover usando rb.MovePosition si no es kinematic para mejor manejo de colisiones,
            // o transform.Translate si es kinematic o no hay Rigidbody.
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

         // Asegurar posición final (más importante si se usa Translate).
         // Podría causar un pequeño salto si hubo colisiones no detectadas por el Raycast inicial.
         // Considera si es necesario o si NetworkTransform suavizará suficiente.
         // transform.position = targetPos; // Comentado por defecto

         // Restaurar estado post-dash.
         if (rb != null) {
             rb.linearVelocity = Vector3.zero; // Detener cualquier velocidad residual.
             rb.useGravity = originalGravity; // Restaurar gravedad.
         }
         isDashing = false; // Marcar fin del dash.
         // Debug.Log($"Player (Servidor {OwnerClientId}): Dash finalizado.");
    }
}
