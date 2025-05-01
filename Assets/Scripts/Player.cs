using UnityEngine;
using Unity.Netcode;
using TMPro;                // Necesario para TMP_Text
using UnityEngine.UI;       // Necesario para Slider
using Unity.Netcode.Components; // Para NetworkTransform
using System.Collections;     // Para Coroutines (Power-ups, Cooldowns)
using System.Collections.Generic; // Para listas (Power-ups activos)

// --- NOTA: Para una implementaci�n completa y organizada, se necesitar�an scripts adicionales: ---
// - GameManager.cs: Para gestionar spawns, colores, estado general de la partida.
// - Bullet.cs: Para la l�gica de la bala (movimiento, colisi�n, color).
// - Door.cs: Para la l�gica de la puerta (estado, interacci�n).
// - PowerUp.cs: Para la l�gica del objeto power-up.
// - PowerUpManager.cs: Para gestionar el spawn de power-ups.
// - MovingPlatform.cs (u otro): Para el escenario din�mico.
// -----------------------------------------------------------------------------------------------

[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(NetworkTransform))]
[RequireComponent(typeof(Rigidbody))] // Necesario para colisiones y potencialmente power-ups
public class Player : NetworkBehaviour
{
    // ==================================
    // --- SECCI�N: Variables Miembro ---
    // ==================================

    [Header("Referencias (Asignar en Inspector)")]
    [SerializeField] private GameObject healthBarPrefab;
    [SerializeField] private Transform healthBarAnchor;
    [SerializeField] private MeshRenderer playerMeshRenderer; // Asignar el renderer principal para cambiar color
    [SerializeField] private Transform firePoint;         // Punto desde donde salen las balas
    [SerializeField] private GameObject bulletPrefab;       // Prefab de la bala (requiere Bullet.cs)
    [SerializeField] private GameObject bulletImpactEffectPrefab; // Efecto visual al chocar la bala
    [SerializeField] private GameObject deathEffectPrefab; // Efecto visual al morir (opcional)
    [SerializeField] private GameObject dashEffectPrefab;  // Efecto visual del dash

    [Header("Movimiento")]
    [SerializeField] private float moveSpeed = 5.0f;

    [Header("Vida")]
    [SerializeField] private int maxHealth = 100;
    [SerializeField] private Color deadColor = Color.gray; // Color al morir

    [Header("Disparo")]
    [SerializeField] private float fireRate = 0.5f; // Segundos entre disparos
    private float nextFireTime = 0f; // Para controlar el cooldown

    [Header("Interacci�n (Puertas)")]
    [SerializeField] private float interactionRadius = 2.0f; // Radio para interactuar con puertas
    [SerializeField] private LayerMask interactableLayer;   // Capa donde est�n los objetos interactuables (puertas)

    [Header("Power-Ups")]
    [SerializeField] private float powerUpDuration = 10.0f; // Duraci�n est�ndar de power-ups
    [SerializeField] private float speedBoostMultiplier = 1.5f;
    [SerializeField] private float fireRateBoostMultiplier = 2f;
    [SerializeField] private int healthBoostAmount = 50;
    [SerializeField] private GameObject speedEffectVisual;  // Objeto/Part�cula a activar para speed boost
    [SerializeField] private GameObject fireRateEffectVisual; // Objeto/Part�cula a activar para fire rate boost
    // (El de vida es instant�neo, quiz�s un peque�o efecto de part�culas al recogerlo)
    private List<ActivePowerUp> activePowerUps = new List<ActivePowerUp>(); // Seguimiento local

    [Header("Mec�nica Nueva (Dash)")]
    [SerializeField] private float dashDistance = 5f;
    [SerializeField] private float dashCooldown = 2f;
    [SerializeField] private float dashDuration = 0.2f; // Tiempo invulnerable/r�pido durante dash
    private float nextDashTime = 0f;
    private bool isDashing = false; // Estado para posible invulnerabilidad

    // --- Network Variables ---
    public NetworkVariable<Color> NetworkColor = new NetworkVariable<Color>(Color.white, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<int> NetworkSalud = new NetworkVariable<int>(100, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<bool> IsDead = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    // Podr�amos necesitar m�s NV para estados de power-ups si el feedback visual externo es complejo

    // --- Referencias Internas ---
    private TMP_Text textoVida;           // UI Local
    private Slider healthBarSlider;       // Barra Flotante
    private GameObject healthBarInstance; // Instancia Barra Flotante
    private Rigidbody rb;                 // Rigidbody del jugador
    private float baseMoveSpeed;          // Para restaurar tras power-up
    private float baseFireRate;           // Para restaurar tras power-up

    // Estructura simple para seguir powerups activos localmente (para revertir efectos)
    private struct ActivePowerUp {
        public PowerUpType Type;
        public Coroutine ExpiryCoroutine;
        public GameObject VisualEffect;
    }
    // Enum para tipos de PowerUp (deber�a estar en un archivo separado o en PowerUp.cs)
    public enum PowerUpType { Speed, FireRate, Health }

    // =========================================
    // --- SECCI�N: M�todos de Ciclo de Vida ---
    // =========================================

    public override void OnNetworkSpawn()
    {
        Debug.Log($"Player Spawned. Owner: {OwnerClientId}, IsOwner: {IsOwner}, IsServer: {IsServer}");

        // Obtener componentes locales
        rb = GetComponent<Rigidbody>();
        if (rb == null) Debug.LogError("Player necesita un Rigidbody.");
        if (playerMeshRenderer == null) Debug.LogWarning("Asignar Player Mesh Renderer en Inspector para cambios de color.");

        // Guardar valores base para Power-ups
        baseMoveSpeed = moveSpeed;
        baseFireRate = fireRate;

        // Configuraci�n inicial de color y estado de muerte
        OnIsDeadChanged(false, IsDead.Value); // Aplicar estado inicial (color)
        ColorChanged(NetworkColor.Value, NetworkColor.Value); // Aplicar color inicial

        // Suscripciones a Network Variables
        NetworkColor.OnValueChanged += ColorChanged;
        NetworkSalud.OnValueChanged += OnHealthChanged;
        IsDead.OnValueChanged += OnIsDeadChanged;

        if (IsOwner)
        {
            // Configuraci�n UI Local
            SetupLocalUI();
            // Solicitar informaci�n inicial al servidor (Color �nico, Posici�n spawn)
            // NOTA: Esto requiere un GameManager en el servidor para funcionar.
            RequestInitialDataServerRpc();
        }
        else
        {
            // Configuraci�n Barra Flotante
            SetupFloatingHealthBar();
        }

        // Verificar configuraci�n NetworkTransform (Server Auth)
        VerifyNetworkTransformConfig();

        // Ajustar f�sicas si no somos el servidor (para evitar control local no autorizado)
        if (!IsServer)
        {
            rb.isKinematic = true; // Los clientes no deben simular f�sica principal
        }
    }

    void Update()
    {
        // El servidor y los clientes actualizan las visuales basadas en NetworkVariables (manejado por callbacks)

        if (!IsOwner || IsDead.Value || isDashing) return; // Solo el propietario no muerto y no haciendo dash puede enviar input

        // --- Input de Movimiento ---
        float horizontalInput = Input.GetAxis("Horizontal");
        float verticalInput = Input.GetAxis("Vertical");
        Vector3 moveDirection = new Vector3(horizontalInput, 0, verticalInput);
        if (moveDirection != Vector3.zero)
        {
            SubmitMovementRequestServerRpc(moveDirection.normalized);
        }

        // --- Input de Disparo ---
        if (Input.GetButton("Fire1") && Time.time >= nextFireTime) // Usar GetButton para disparo continuo si se mantiene
        {
            // Comprobaci�n de cooldown local r�pida (el servidor tiene la �ltima palabra)
            nextFireTime = Time.time + fireRate;
            // Obtener direcci�n de disparo (basada en la c�mara o el forward del jugador)
            // Ejemplo simple: Hacia donde mira el jugador
            SubmitFireRequestServerRpc(firePoint.position, firePoint.rotation);
        }

        // --- Input de Interacci�n (Puertas) ---
        if (Input.GetKeyDown(KeyCode.E))
        {
            SubmitInteractionRequestServerRpc();
        }

        // --- Input de Mec�nica Nueva (Dash) ---
        if (Input.GetKeyDown(KeyCode.LeftShift) && Time.time >= nextDashTime)
        {
             Vector3 dashDir = moveDirection.normalized;
             // Si no hay input de movimiento, usar la direcci�n hacia donde mira el jugador
             if (dashDir == Vector3.zero) dashDir = transform.forward;
             nextDashTime = Time.time + dashCooldown;
             SubmitDashRequestServerRpc(dashDir);
        }
    }

     // Detecci�n de colisiones (para Power-ups principalmente en el servidor)
    private void OnTriggerEnter(Collider other)
    {
        // La detecci�n de PowerUps deber�a hacerla el servidor
        if (!IsServer) return;

        // --- L�gica de Recogida de PowerUp ---
        // Esto ASUME que existe un script PowerUp.cs en el objeto con el trigger
        // y que tiene un m�todo p�blico para obtener su tipo.
        /* --- REQUIERE PowerUp.cs ---
        if (other.CompareTag("PowerUp")) // Asignar tag "PowerUp" a los prefabs de PowerUp
        {
            PowerUp powerUp = other.GetComponent<PowerUp>();
            if (powerUp != null && powerUp.CanBePickedUp())
            {
                PowerUpType type = powerUp.GetPowerUpType();
                Debug.Log($"Servidor: Jugador {OwnerClientId} recogi� PowerUp tipo {type}");

                // Aplicar efecto en el servidor
                ApplyPowerUpEffect(type);

                // Decirle al PowerUp que fue recogido (para que desaparezca y se sincronice)
                powerUp.Pickup(this.gameObject); // O simplemente destruirlo/despawnearlo aqu�

                // Enviar RPC al cliente propietario para feedback y efectos locales
                NotifyPowerUpPickupClientRpc(type);
            }
        }
        */ // --- FIN REQUIERE PowerUp.cs ---
    }


    public override void OnNetworkDespawn()
    {
        // Desuscripciones
        if (NetworkColor != null) NetworkColor.OnValueChanged -= ColorChanged;
        if (NetworkSalud != null) NetworkSalud.OnValueChanged -= OnHealthChanged;
        if (IsDead != null) IsDead.OnValueChanged -= OnIsDeadChanged;

        // Limpiar barra flotante
        if (healthBarInstance != null) Destroy(healthBarInstance);

        // Limpiar coroutines locales si es necesario (powerups)
        foreach(var powerup in activePowerUps) {
            if (powerup.ExpiryCoroutine != null) StopCoroutine(powerup.ExpiryCoroutine);
        }
        activePowerUps.Clear();

        base.OnNetworkDespawn();
    }

    // =================================================
    // --- SECCI�N: Configuraci�n Inicial y UI Local ---
    // =================================================

    private void SetupLocalUI()
    {
        var vidaPlayerObject = GameObject.Find("VidaPlayer"); // Buscar objeto UI por nombre
        if (vidaPlayerObject != null)
        {
            textoVida = vidaPlayerObject.GetComponent<TMP_Text>();
            if (textoVida == null) Debug.LogError("No se encontr� TMP_Text en 'VidaPlayer'");
            else UpdateLocalHealthUI(NetworkSalud.Value); // Actualizar UI inicial
        }
        else Debug.LogError("No se encontr� 'VidaPlayer' en la escena para UI local.");
    }

    private void SetupFloatingHealthBar()
    {
        if (healthBarPrefab != null && healthBarAnchor != null)
        {
            healthBarInstance = Instantiate(healthBarPrefab, healthBarAnchor.position, healthBarAnchor.rotation);
            healthBarInstance.transform.SetParent(healthBarAnchor, true);
            healthBarSlider = healthBarInstance.GetComponentInChildren<Slider>();
            if (healthBarSlider == null) Debug.LogError("Prefab de barra de vida no tiene Slider.");
            else UpdateFloatingHealthBar(NetworkSalud.Value); // Actualizar barra inicial
        }
        else Debug.LogError("Falta healthBarPrefab o healthBarAnchor en el Inspector.");
    }

     private void VerifyNetworkTransformConfig()
     {
        var networkTransform = GetComponent<NetworkTransform>();
        if (networkTransform == null) Debug.LogError("NetworkTransform no encontrado, pero es requerido.");
        // En versiones sin AuthorityMode, IsServerAuthoritative es el �nico chequeo
        else if (!networkTransform.IsServerAuthoritative())
        {
             Debug.LogWarning($"�CONFIGURACI�N INCORRECTA! NetworkTransform en {gameObject.name} NO est� configurado como Server Authoritative. " +
                              $"El PDF requiere autoridad del servidor. Aseg�rate que tu versi�n de Netcode lo use por defecto o config�ralo en el Inspector si la opci�n existe.");
        }
     }

    // ===============================================
    // --- SECCI�N: Callbacks de Network Variables ---
    // ===============================================

    // Se llama en TODOS los clientes cuando NetworkSalud cambia
    private void OnHealthChanged(int previousValue, int newValue)
    {
        if (IsOwner) UpdateLocalHealthUI(newValue);
        else UpdateFloatingHealthBar(newValue);
    }

    // Se llama en TODOS los clientes cuando NetworkColor cambia
    private void ColorChanged(Color previousValue, Color newValue)
    {
        if (playerMeshRenderer != null)
        {
             // Si est� muerto, el deadColor tiene prioridad
            playerMeshRenderer.material.color = IsDead.Value ? deadColor : newValue;
        }
    }

    // Se llama en TODOS los clientes cuando IsDead cambia
    private void OnIsDeadChanged(bool previousValue, bool newValue)
    {
        bool justDied = newValue && !previousValue;
        bool justRespawned = !newValue && previousValue; // Para futura l�gica de respawn

        if (playerMeshRenderer != null)
        {
            // Aplicar color de muerto o el color de NetworkColor
            playerMeshRenderer.material.color = newValue ? deadColor : NetworkColor.Value;
        }

        // Opcional: Desactivar/Activar componentes visuales o colisionadores
        GetComponent<Collider>().enabled = !newValue; // No colisionar si est� muerto
        // Podr�as desactivar el renderer excepto si eres el due�o para verlo gris
        if (playerMeshRenderer != null) playerMeshRenderer.enabled = !newValue || IsOwner;
        if (healthBarInstance != null) healthBarInstance.SetActive(!newValue); // Ocultar barra de vida al morir

        if (justDied && IsServer) {
            // L�gica del servidor al morir (ej. contar muertes, iniciar respawn timer)
            Debug.Log($"Servidor: Jugador {OwnerClientId} ha muerto.");
            // Instanciar efecto de muerte si existe
            if(deathEffectPrefab != null) {
                GameObject deathFx = Instantiate(deathEffectPrefab, transform.position, Quaternion.identity);
                NetworkObject deathFxNetObj = deathFx.GetComponent<NetworkObject>();
                 if (deathFxNetObj != null) deathFxNetObj.Spawn(true); // Spawn y destruir autom�ticamente
                 else Destroy(deathFx, 3f); // Destruir si no es objeto de red
            }
        }

         if (justDied && IsOwner) {
             // L�gica del cliente propietario al morir (ej. mostrar mensaje "Has muerto")
             Debug.Log("�Has Muerto!");
             // Detener efectos visuales de powerups locales
             StopAllPowerupVisuals();
         }
    }


    // ===========================================
    // --- SECCI�N: Actualizaci�n de UI / Visual ---
    // ===========================================

    private void UpdateLocalHealthUI(int currentHealth)
    {
        if (textoVida != null) textoVida.text = "HEALTH: " + currentHealth.ToString();
    }

    private void UpdateFloatingHealthBar(int currentHealth)
    {
        if (healthBarSlider != null) healthBarSlider.value = (float)currentHealth / maxHealth;
        if (healthBarInstance != null) healthBarInstance.SetActive(currentHealth > 0 && !IsDead.Value); // Ocultar si muerto o sin vida
    }

    // ============================================
    // --- SECCI�N: Server RPCs (Cliente llama) ---
    // ============================================

    // --- Inicio de Partida (Requiere GameManager) ---
    [ServerRpc]
    private void RequestInitialDataServerRpc(ServerRpcParams rpcParams = default)
    {
        ulong clientId = rpcParams.Receive.SenderClientId;
        Debug.Log($"Servidor: Recibida petici�n de datos iniciales de {clientId}");

        // --- REQUIERE GameManager.cs ---
        /*
        GameManager gm = FindObjectOfType<GameManager>(); // Ineficiente, mejor tener referencia
        if (gm != null)
        {
            // 1. Asignar Color �nico
            Color assignedColor = gm.AssignUniqueColor(clientId);
            NetworkColor.Value = assignedColor; // Sincroniza a todos

            // 2. Asignar Posici�n de Spawn �nica
            Vector3 spawnPos = gm.AssignSpawnPoint(clientId);
            if (spawnPos != Vector3.zero) // Verificar si se pudo asignar
            {
                // Teletransportar al jugador (NetworkTransform sincronizar�)
                transform.position = spawnPos;
                // Opcional: Enviar un ClientRpc para forzar la posici�n si NetworkTransform tarda
                // TeleportClientRpc(spawnPos);
            }
            else {
                 Debug.LogError($"Servidor: No se pudo asignar spawn point para {clientId}");
            }
        }
        else {
             Debug.LogError("Servidor: GameManager no encontrado para asignar datos iniciales.");
        }
        */ // --- FIN REQUIERE GameManager.cs ---
    }

    // --- Movimiento ---
    [ServerRpc]
    void SubmitMovementRequestServerRpc(Vector3 direction, ServerRpcParams rpcParams = default)
    {
        // Ignorar si el cliente que envi� el RPC est� muerto
        if (IsDead.Value) return;

        // Mover en el servidor
        Vector3 movement = direction * moveSpeed * Time.deltaTime;
        transform.Translate(movement, Space.World);
        // Alternativa: rb.MovePosition(rb.position + movement); // Si se usa f�sicas m�s activamente
    }

    // --- Disparo (Requiere Bullet.cs) ---
    [ServerRpc]
    void SubmitFireRequestServerRpc(Vector3 spawnPos, Quaternion spawnRot, ServerRpcParams rpcParams = default)
    {
        ulong clientId = rpcParams.Receive.SenderClientId;
        if (IsDead.Value) return; // Ignorar si muerto

        // Comprobar cooldown real en el servidor
        // (Podr�amos necesitar una variable de '�ltimo disparo' por jugador si hay muchos)
        // Simplificaci�n: Usamos una variable local del servidor en el objeto Player
        // Esto no es perfecto si hay lag, el cliente podr�a disparar antes visualmente.
        // Una NetworkVariable para nextFireTime ser�a m�s precisa pero m�s compleja.

        Debug.Log($"Servidor: Recibido disparo de {clientId}. Tiempo actual: {Time.time}, Pr�ximo disparo permitido: {nextFireTime}");
        // REVISAR COOLDOWN: La variable nextFireTime es local en cada instancia Player.
        // El chequeo local en Update() previene spam, pero el servidor deber�a tener la autoridad final.
        // Para un cooldown *real* gestionado por servidor, necesitar�amos almacenar el 'lastFireTime'
        // en una variable *del servidor* asociada a este jugador.

        // --- Comienzo L�gica de Disparo ---
        Debug.Log($"Servidor: Procesando disparo para {clientId}.");

        // --- REQUIERE Bullet.cs ---
        /*
        if (bulletPrefab != null)
        {
            GameObject bulletInstance = Instantiate(bulletPrefab, spawnPos, spawnRot);
            NetworkObject bulletNetworkObject = bulletInstance.GetComponent<NetworkObject>();
            Bullet bulletScript = bulletInstance.GetComponent<Bullet>();

            if (bulletNetworkObject != null && bulletScript != null)
            {
                // Configurar la bala antes de hacerla visible en red
                bulletScript.Initialize(OwnerClientId, NetworkColor.Value, bulletImpactEffectPrefab); // Pasar ID due�o, color, y efecto

                // Hacer spawn de la bala en la red
                bulletNetworkObject.Spawn(); // El servidor ahora es el due�o de la bala
                Debug.Log($"Servidor: Bala instanciada y spawneada para {clientId}");
            }
            else {
                 Debug.LogError("Prefab de bala no tiene NetworkObject o Bullet script.");
                 Destroy(bulletInstance); // Limpiar si falla
            }
        }
        else {
             Debug.LogError("Prefab de bala no asignado en el Inspector del jugador.");
        }
        */ // --- FIN REQUIERE Bullet.cs ---
    }

    // --- Interacci�n (Requiere Door.cs) ---
    [ServerRpc]
    void SubmitInteractionRequestServerRpc(ServerRpcParams rpcParams = default)
    {
        if (IsDead.Value) return; // Ignorar si muerto

        Debug.Log($"Servidor: Jugador {OwnerClientId} intent� interactuar.");
        // Buscar objetos interactuables cercanos
        Collider[] hits = Physics.OverlapSphere(transform.position, interactionRadius, interactableLayer);
        GameObject closestInteractable = null;
        float minDistance = float.MaxValue;

        foreach (var hit in hits)
        {
            // --- REQUIERE Door.cs (o una interfaz IInteractable) ---
            /*
            // Asumimos que las puertas tienen el script Door.cs
            Door door = hit.GetComponent<Door>();
            if (door != null)
            {
                // Comprobar visibilidad (simple raycast desde el jugador a la puerta)
                RaycastHit visibilityHit;
                Vector3 directionToDoor = (hit.transform.position - transform.position).normalized;
                if (Physics.Raycast(transform.position + Vector3.up * 0.5f, // Origen raycast ligeramente elevado
                                    directionToDoor,
                                    out visibilityHit,
                                    interactionRadius * 1.1f)) // Un poco m�s de rango para el raycast
                {
                    if (visibilityHit.collider == hit) // �Realmente vemos la puerta?
                    {
                         float distance = Vector3.Distance(transform.position, hit.transform.position);
                         if (distance < minDistance) {
                             minDistance = distance;
                             closestInteractable = hit.gameObject;
                         }
                    }
                }
            }
            */ // --- FIN REQUIERE Door.cs ---
        }

        // Si encontramos una puerta interactuable y visible
        if (closestInteractable != null)
        {
            Debug.Log($"Servidor: Jugador {OwnerClientId} interactuando con {closestInteractable.name}");
             // --- REQUIERE Door.cs ---
            /*
            Door doorToToggle = closestInteractable.GetComponent<Door>();
            if (doorToToggle != null) {
                doorToToggle.ToggleDoorState(); // El m�todo en Door.cs manejar�a la l�gica y sincronizaci�n
            }
            */ // --- FIN REQUIERE Door.cs ---
        }
        else {
             Debug.Log($"Servidor: Jugador {OwnerClientId} no encontr� nada con qu� interactuar.");
        }
    }

     // --- Mec�nica Nueva (Dash) ---
     [ServerRpc]
     void SubmitDashRequestServerRpc(Vector3 direction) {
         if (IsDead.Value || isDashing) return; // Ignorar si muerto o ya haciendo dash

         StartCoroutine(PerformDashServer(direction));
         // Enviar RPC a todos para efecto visual
         NotifyDashClientRpc(direction);
     }

    // ===========================================
    // --- SECCI�N: Client RPCs (Servidor llama) ---
    // ===========================================

    // --- Teletransporte Inicial (Opcional, si NetworkTransform tarda) ---
    [ClientRpc]
    private void TeleportClientRpc(Vector3 position, ClientRpcParams rpcParams = default)
    {
        // Solo ejecutar si somos el propietario de este objeto
        if (!IsOwner) return;
        transform.position = position;
        Debug.Log($"Cliente {OwnerClientId}: Teletransportado a {position} por el servidor.");
    }

    // --- Notificar Recogida de PowerUp (para feedback local) ---
    [ClientRpc]
    private void NotifyPowerUpPickupClientRpc(PowerUpType type, ClientRpcParams rpcParams = default)
    {
        // Solo el cliente propietario necesita reaccionar a esto para su propia UI/efectos
        if (!IsOwner) return;

        Debug.Log($"Cliente: �Recogiste un PowerUp de tipo {type}!");
        // Aqu� podr�as mostrar un texto en pantalla, reproducir un sonido, etc.
        // El efecto *real* (velocidad, etc.) se aplica/revierte localmente en la corutina Start/StopPowerUpEffect
        StartCoroutine(StartPowerUpEffectLocal(type));
    }


    // --- Notificar Impacto de Bala (Solo en clientes) ---
    // ESTE RPC DEBER�A LLAMARSE DESDE Bullet.cs CUANDO COLISIONA, NO DESDE Player.cs
    /*
    [ClientRpc]
    public void SpawnImpactEffectClientRpc(Vector3 position, Vector3 normal) {
        // El servidor NO deber�a ejecutar esto
        if (IsServer) return;

        Debug.Log("Cliente: Creando efecto de impacto de bala");
        if (bulletImpactEffectPrefab != null) {
            Instantiate(bulletImpactEffectPrefab, position, Quaternion.LookRotation(normal));
        }
    }
    */

     // --- Notificar Dash (para efecto visual en todos) ---
     [ClientRpc]
     void NotifyDashClientRpc(Vector3 direction) {
         // Crear efecto visual del dash en todos los clientes
         if (dashEffectPrefab != null) {
             // Orientar el efecto en la direcci�n del dash
             GameObject dashFx = Instantiate(dashEffectPrefab, transform.position, Quaternion.LookRotation(direction));
             Destroy(dashFx, 1.0f); // Autodestruir el efecto
         }
         // Podr�amos iniciar una peque�a corutina local para feedback adicional si es necesario
     }

    // ========================================
    // --- SECCI�N: L�gica de Juego (Vida) ---
    // ========================================

    // M�todo llamado por otros scripts (ej. la bala en el servidor) para da�ar al jugador
    public void TakeDamage(int amount)
    {
        if (!IsServer || IsDead.Value || isDashing) // No da�ar si muerto o haciendo dash (invulnerable)
        {
            // Si no es servidor, o est� muerto/dashing, ignorar.
            // Podr�amos querer loggear el intento si no es servidor.
            // if (!IsServer) Debug.LogWarning("TakeDamage llamado desde un no-servidor.");
            return;
        }

        int previousHealth = NetworkSalud.Value;
        int newHealth = previousHealth - amount;
        NetworkSalud.Value = Mathf.Max(0, newHealth); // Asegura que no sea negativa

        Debug.Log($"Servidor: Jugador {OwnerClientId} recibi� {amount} da�o. Vida: {previousHealth} -> {NetworkSalud.Value}");

        // Comprobar si ha muerto con este da�o
        if (NetworkSalud.Value <= 0 && previousHealth > 0)
        {
            Die(); // Llama a la funci�n que maneja la muerte
        }
    }

    // L�gica ejecutada en el servidor cuando la vida llega a 0
    private void Die()
    {
        if (!IsServer || IsDead.Value) return; // Solo el servidor puede matar y solo una vez

        Debug.Log($"Servidor: Matando al jugador {OwnerClientId}");
        IsDead.Value = true; // Esto sincronizar� el estado y disparar� OnIsDeadChanged en todos

        // Aqu� ir�a l�gica adicional del servidor al morir:
        // - Detener movimiento f�sico si es necesario: rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
        // - Contar muerte en GameManager
        // - Iniciar temporizador de respawn (si aplica)
        //   StartCoroutine(RespawnTimer(5.0f)); // Ejemplo
    }

     // L�gica de Respawn (Ejemplo b�sico, requiere m�s trabajo)
     private IEnumerator RespawnTimer(float delay) {
         yield return new WaitForSeconds(delay);
         Respawn();
     }

     private void Respawn() {
         if (!IsServer) return;
         Debug.Log($"Servidor: Respawneando jugador {OwnerClientId}");

         // --- REQUIERE GameManager.cs ---
         /*
         GameManager gm = FindObjectOfType<GameManager>();
         if (gm != null) {
             // Obtener una nueva posici�n de spawn (podr�a ser aleatoria o la original)
             Vector3 spawnPos = gm.GetRandomSpawnPoint(); // Necesitar�a este m�todo
             transform.position = spawnPos;
             // TeleportClientRpc(spawnPos); // Opcional
         } else {
              transform.position = Vector3.zero; // Fallback
         }
         */ // --- FIN REQUIERE GameManager.cs ---

         // Restaurar estado
         NetworkSalud.Value = maxHealth;
         IsDead.Value = false; // Esto dispara OnIsDeadChanged y restaura apariencia/colisiones
     }


    // M�todo original del usuario para quitar vida (usado por HUD)
    public void QuitarVida(int cantidad)
    {
        // Redirige a TakeDamage asegurando que solo el servidor lo procese
        TakeDamage(cantidad);
    }

    // ===========================================
    // --- SECCI�N: L�gica de Power-Ups (Local) ---
    // ===========================================
    // NOTA: La l�gica de aplicar el efecto REAL (cambio de stats) debe ocurrir
    // tanto en el servidor (para autoridad) como en el cliente propietario (para feedback inmediato).
    // La corutina local gestiona la duraci�n y la reversi�n del efecto local.

    // Llamado por RPC cuando el propietario recoge un powerup
    private IEnumerator StartPowerUpEffectLocal(PowerUpType type)
    {
        Debug.Log($"Cliente: Aplicando efecto local para {type}");
        GameObject visualEffectInstance = null;
        float currentDuration = powerUpDuration; // Podr�a variar por powerup

        // Aplicar efecto VISUAL y de STATS localmente
        switch (type)
        {
            case PowerUpType.Speed:
                moveSpeed = baseMoveSpeed * speedBoostMultiplier;
                if (speedEffectVisual != null) speedEffectVisual.SetActive(true);
                visualEffectInstance = speedEffectVisual;
                break;
            case PowerUpType.FireRate:
                fireRate = baseFireRate / fireRateBoostMultiplier; // Dividir para aumentar la cadencia
                 if (fireRateEffectVisual != null) fireRateEffectVisual.SetActive(true);
                 visualEffectInstance = fireRateEffectVisual;
                break;
            case PowerUpType.Health:
                // La vida ya fue actualizada por el servidor v�a NetworkVariable.
                // Aqu� podr�amos mostrar un efecto visual instant�neo de curaci�n.
                 Debug.Log("Cliente: Efecto de vida ya aplicado por NetworkVariable.");
                 // No necesita corutina de duraci�n para el stat, pero s� para el visual si lo hay.
                 // yield break; // Si no hay efecto visual de duraci�n
                break;
        }

         // A�adir a la lista de activos para revertir luego
         ActivePowerUp newActivePowerUp = new ActivePowerUp { Type = type, VisualEffect = visualEffectInstance };
         Coroutine expiry = StartCoroutine(RevertPowerUpEffectLocal(type, currentDuration, newActivePowerUp));
         newActivePowerUp.ExpiryCoroutine = expiry;
         activePowerUps.Add(newActivePowerUp);

         yield return null; // Necesario para que sea una Coroutine v�lida si no hay yield break antes
    }

    private IEnumerator RevertPowerUpEffectLocal(PowerUpType type, float duration, ActivePowerUp trackingInfo)
    {
        yield return new WaitForSeconds(duration);

        Debug.Log($"Cliente: Revirtiendo efecto local para {type}");
        // Revertir STATS locales
        switch (type)
        {
            case PowerUpType.Speed:
                moveSpeed = baseMoveSpeed;
                break;
            case PowerUpType.FireRate:
                fireRate = baseFireRate;
                break;
            case PowerUpType.Health:
                // No hay stat que revertir
                break;
        }
        // Desactivar efecto VISUAL local
        if (trackingInfo.VisualEffect != null)
        {
            trackingInfo.VisualEffect.SetActive(false);
        }
        // Eliminar de la lista de seguimiento
        activePowerUps.Remove(trackingInfo);
    }

     // Para detener efectos al morir, etc.
     private void StopAllPowerupVisuals() {
         foreach(var powerup in activePowerUps) {
             if (powerup.VisualEffect != null) powerup.VisualEffect.SetActive(false);
             if (powerup.ExpiryCoroutine != null) StopCoroutine(powerup.ExpiryCoroutine);
             // No revertimos stats aqu�, se resetean al respawnear o al inicio
         }
         activePowerUps.Clear();
     }

     // --- L�gica de Power-Ups (Servidor - Aplicaci�n Real) ---
     // ESTA L�GICA DEBER�A LLAMARSE EN EL SERVIDOR CUANDO OnTriggerEnter detecta el PowerUp
     private void ApplyPowerUpEffect(PowerUpType type) {
         if (!IsServer) return;

         // Aplicar efecto en el servidor (la NetworkVariable se encarga de la vida)
         // Para velocidad y cadencia, necesitamos afectar las variables usadas en el servidor.
         // Podr�amos usar corutinas en el servidor tambi�n para la duraci�n.
         Debug.Log($"Servidor: Aplicando efecto {type} a {OwnerClientId}");
         switch(type) {
             case PowerUpType.Speed:
                 StartCoroutine(ServerPowerUpDuration(type, powerUpDuration));
                 break;
             case PowerUpType.FireRate:
                  StartCoroutine(ServerPowerUpDuration(type, powerUpDuration));
                 break;
             case PowerUpType.Health:
                 NetworkSalud.Value = Mathf.Min(maxHealth, NetworkSalud.Value + healthBoostAmount);
                 break;
         }
     }

     private IEnumerator ServerPowerUpDuration(PowerUpType type, float duration) {
         // Aplicar efecto en servidor
         switch(type) {
             case PowerUpType.Speed: moveSpeed = baseMoveSpeed * speedBoostMultiplier; break;
             case PowerUpType.FireRate: fireRate = baseFireRate / fireRateBoostMultiplier; break;
         }

         yield return new WaitForSeconds(duration);

         // Revertir efecto en servidor
         switch(type) {
             case PowerUpType.Speed: moveSpeed = baseMoveSpeed; break;
             case PowerUpType.FireRate: fireRate = baseFireRate; break;
         }
          Debug.Log($"Servidor: Efecto {type} expirado para {OwnerClientId}");
     }


    // =====================================================
    // --- SECCI�N: L�gica de Mec�nica Nueva (Dash Server) ---
    // =====================================================

     private IEnumerator PerformDashServer(Vector3 direction) {
         if (!IsServer) yield break;

         isDashing = true; // Marcar como 'dashing' (�til para invulnerabilidad)

         // Guardar velocidad original y desactivar gravedad temporalmente si usamos f�sicas
         // Vector3 originalVelocity = rb.velocity;
         bool originalGravity = rb.useGravity;
         rb.useGravity = false;

         // Aplicar impulso o teletransporte r�pido
         // Opci�n 1: Impulso fuerte
         // rb.velocity = direction * (dashDistance / dashDuration); // Velocidad necesaria para cubrir distancia en tiempo

         // Opci�n 2: Mover con Translate (m�s simple si no hay mucha f�sica compleja)
         float elapsedTime = 0f;
         Vector3 startPos = transform.position;
         Vector3 targetPos = transform.position + direction * dashDistance;

         // Raycast para evitar atravesar paredes
         RaycastHit hit;
         if (Physics.Raycast(startPos, direction, out hit, dashDistance)) {
             targetPos = hit.point - direction * 0.1f; // Detenerse justo antes del obst�culo
         }

         while (elapsedTime < dashDuration) {
             // Interpolar posici�n (o simplemente mover a toda velocidad)
             // transform.position = Vector3.Lerp(startPos, targetPos, elapsedTime / dashDuration);
             // Mover una fracci�n de la distancia total cada frame
             transform.Translate(direction * (dashDistance / dashDuration) * Time.deltaTime, Space.World);

             elapsedTime += Time.deltaTime;
             yield return null; // Esperar al siguiente frame
         }

         // Asegurarse de que termina en la posici�n final (o la ajustada por colisi�n)
         // transform.position = targetPos; // Puede causar saltos si la f�sica interfiere

         // Restaurar estado
         // rb.velocity = originalVelocity; // O poner a cero? Depende del dise�o
         rb.linearVelocity = Vector3.zero; // Detener movimiento brusco post-dash
         rb.useGravity = originalGravity;
         isDashing = false;
          Debug.Log($"Servidor: Jugador {OwnerClientId} termin� dash.");
     }
}
