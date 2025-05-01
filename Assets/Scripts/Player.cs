using UnityEngine;
using Unity.Netcode;
using TMPro;
using UnityEngine.UI;
using Unity.Netcode.Components;
using System.Collections;
using System.Collections.Generic;

// Enum movido fuera de la clase Player para que sea mas facil acceso.
public enum PowerUpType { Speed, FireRate, Health }

[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(NetworkTransform))]
[RequireComponent(typeof(Rigidbody))]
public class Player : NetworkBehaviour
{
    // ==================================
    // --- SECCIÓN: Variables Miembro ---
    // ==================================

    [Header("Referencias (Asignar en Inspector)")]
    [SerializeField] private GameObject healthBarPrefab;
    [SerializeField] private Transform healthBarAnchor;
    [SerializeField] private MeshRenderer playerMeshRenderer;
    [SerializeField] private Transform firePoint;
    [SerializeField] private GameObject bulletPrefab; // REQUIRES Bullet.cs prefab
    [SerializeField] private GameObject bulletImpactEffectPrefab;
    [SerializeField] private GameObject deathEffectPrefab;
    [SerializeField] private GameObject dashEffectPrefab;

    [Header("Movimiento")]
    [SerializeField] private float moveSpeed = 5.0f;

    [Header("Vida")]
    [SerializeField] private int maxHealth = 100;
    [SerializeField] private Color deadColor = Color.gray;

    [Header("Disparo")]
    [SerializeField] private float fireRate = 0.5f;
    private float nextFireTime = 0f;

    [Header("Interacción (Puertas)")]
    [SerializeField] private float interactionRadius = 2.0f;
    [SerializeField] private LayerMask interactableLayer;

    [Header("Power-Ups")]
    [SerializeField] private float powerUpDuration = 10.0f;
    [SerializeField] private float speedBoostMultiplier = 1.5f;
    [SerializeField] private float fireRateBoostMultiplier = 2f;
    [SerializeField] private int healthBoostAmount = 50;
    [SerializeField] private GameObject speedEffectVisual;
    [SerializeField] private GameObject fireRateEffectVisual;
    private List<ActivePowerUp> activePowerUps = new List<ActivePowerUp>();

    [Header("Mecánica Nueva (Dash)")]
    [SerializeField] private float dashDistance = 5f;
    [SerializeField] private float dashCooldown = 2f;
    [SerializeField] private float dashDuration = 0.2f;
    private float nextDashTime = 0f;
    private bool isDashing = false;

    // --- Network Variables ---
    public NetworkVariable<Color> NetworkColor = new NetworkVariable<Color>(Color.white, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<int> NetworkSalud = new NetworkVariable<int>(100, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<bool> IsDead = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // --- Referencias Internas ---
    private TMP_Text textoVida;
    private Slider healthBarSlider;
    private GameObject healthBarInstance;
    private Rigidbody rb;
    private float baseMoveSpeed;
    private float baseFireRate;

    // --- Propiedades Públicas (Getters) ---
    public int MaxHealth => maxHealth;
    public int HealthBoostAmount => healthBoostAmount;

    // Estructura simple para seguir powerups activos localmente
    private struct ActivePowerUp {
        public PowerUpType Type;
        public Coroutine ExpiryCoroutine;
        public GameObject VisualEffect;
    }

    // =========================================
    // --- SECCIÓN: Métodos de Ciclo de Vida ---
    // =========================================

    public override void OnNetworkSpawn()
    {
        rb = GetComponent<Rigidbody>();
        if (rb == null) Debug.LogError("Player necesita un Rigidbody.");
        if (playerMeshRenderer == null) Debug.LogWarning("Asignar Player Mesh Renderer en Inspector para cambios de color.");

        baseMoveSpeed = moveSpeed;
        baseFireRate = fireRate;

        OnIsDeadChanged(false, IsDead.Value);
        ColorChanged(NetworkColor.Value, NetworkColor.Value);

        NetworkColor.OnValueChanged += ColorChanged;
        NetworkSalud.OnValueChanged += OnHealthChanged;
        IsDead.OnValueChanged += OnIsDeadChanged;

        if (IsOwner)
        {
            SetupLocalUI();
            RequestInitialDataServerRpc();
        }
        else
        {
            SetupFloatingHealthBar();
        }

        VerifyNetworkTransformConfig();

        if (!IsServer) rb.isKinematic = true;
    }

    void Update()
    {
        if (!IsOwner || IsDead.Value || isDashing) return;

        // Input Movimiento
        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");
        Vector3 moveDir = new Vector3(h, 0, v);
        if (moveDir != Vector3.zero) SubmitMovementRequestServerRpc(moveDir.normalized);

        // Input Disparo
        if (Input.GetButton("Fire1") && Time.time >= nextFireTime)
        {
            nextFireTime = Time.time + fireRate; // Cooldown local rápido
            // Comprobar si firePoint está asignado antes de usarlo
            if (firePoint != null) {
                SubmitFireRequestServerRpc(firePoint.position, firePoint.rotation);
            } else {
                Debug.LogError($"Jugador {OwnerClientId}: Fire Point no está asignado en el Inspector.");
            }
        }

        // Input Interacción
        if (Input.GetKeyDown(KeyCode.E)) SubmitInteractionRequestServerRpc();

        // Input Dash
        if (Input.GetKeyDown(KeyCode.LeftShift) && Time.time >= nextDashTime)
        {
             Vector3 dashDir = moveDir.normalized;
             if (dashDir == Vector3.zero) dashDir = transform.forward;
             nextDashTime = Time.time + dashCooldown;
             SubmitDashRequestServerRpc(dashDir);
        }
    }

    public override void OnNetworkDespawn()
    {
        if (NetworkColor != null) NetworkColor.OnValueChanged -= ColorChanged;
        if (NetworkSalud != null) NetworkSalud.OnValueChanged -= OnHealthChanged;
        if (IsDead != null) IsDead.OnValueChanged -= OnIsDeadChanged;
        if (healthBarInstance != null) Destroy(healthBarInstance);
        StopAllPowerupCoroutinesAndVisuals(); // Limpiar powerups locales
        base.OnNetworkDespawn();
    }


    // =================================================
    // --- SECCIÓN: Configuración Inicial y UI Local ---
    // =================================================
    private void SetupLocalUI()
    {
        var vidaPlayerObject = GameObject.Find("VidaPlayer");
        if (vidaPlayerObject != null) {
            textoVida = vidaPlayerObject.GetComponent<TMP_Text>();
            if (textoVida != null) UpdateLocalHealthUI(NetworkSalud.Value);
            else Debug.LogError("No se encontró TMP_Text en 'VidaPlayer'");
        } else Debug.LogError("No se encontró 'VidaPlayer' en la escena.");
    }

    private void SetupFloatingHealthBar()
    {
        if (healthBarPrefab != null && healthBarAnchor != null) {
            healthBarInstance = Instantiate(healthBarPrefab, healthBarAnchor.position, healthBarAnchor.rotation);
            healthBarInstance.transform.SetParent(healthBarAnchor, true);
            healthBarSlider = healthBarInstance.GetComponentInChildren<Slider>();
            if (healthBarSlider != null) UpdateFloatingHealthBar(NetworkSalud.Value);
            else Debug.LogError("Prefab de barra de vida no tiene Slider.");
        } else Debug.LogError("Falta healthBarPrefab o healthBarAnchor en el Inspector.");
    }

     private void VerifyNetworkTransformConfig() {
        var networkTransform = GetComponent<NetworkTransform>();
        if (networkTransform == null) Debug.LogError("NetworkTransform no encontrado.");
        else if (!networkTransform.IsServerAuthoritative())
             Debug.LogWarning($"¡CONFIGURACIÓN INCORRECTA! NetworkTransform en {gameObject.name} NO es Server Authoritative.");
     }

    // ===============================================
    // --- SECCIÓN: Callbacks de Network Variables ---
    // ===============================================
     private void OnHealthChanged(int previousValue, int newValue)
    {
        if (IsOwner) UpdateLocalHealthUI(newValue);
        else UpdateFloatingHealthBar(newValue);
    }

    private void ColorChanged(Color previousValue, Color newValue)
    {
        if (playerMeshRenderer != null)
            playerMeshRenderer.material.color = IsDead.Value ? deadColor : newValue;
    }

    private void OnIsDeadChanged(bool previousValue, bool newValue)
    {
        bool justDied = newValue && !previousValue;

        if (playerMeshRenderer != null)
            playerMeshRenderer.material.color = newValue ? deadColor : NetworkColor.Value;

        // --- Corrección Muerte/Caída ---
        //GetComponent<Collider>().enabled = !newValue; // No desactivar collider principal
        if (rb != null) {
            rb.isKinematic = newValue; // Hacer kinemático al morir
            if (newValue) { // Si muere
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            } else if (IsServer) { // Si revive (y somos servidor)
                rb.isKinematic = false; // Restaurar física si el servidor no es kinemático por defecto
            }
        }
        // --- Fin Corrección ---

        if (healthBarInstance != null) healthBarInstance.SetActive(!newValue);

        if (justDied) {
            if (IsServer && deathEffectPrefab != null) {
                GameObject deathFx = Instantiate(deathEffectPrefab, transform.position, Quaternion.identity);
                NetworkObject deathFxNetObj = deathFx.GetComponent<NetworkObject>();
                 if (deathFxNetObj != null) deathFxNetObj.Spawn(true);
                 else Destroy(deathFx, 3f);
            }
            if (IsOwner) {
                 Debug.Log("¡Has Muerto!");
                 StopAllPowerupCoroutinesAndVisuals();
            }
             // --- Corrección Muerte/Respawn ---
             // No iniciar respawn automático aquí para poder ver al muerto
             // StartCoroutine(RespawnTimer(5.0f));
             // --- Fin Corrección ---
        }
    }

    // ===========================================
    // --- SECCIÓN: Actualización de UI / Visual ---
    // ===========================================
    private void UpdateLocalHealthUI(int currentHealth) {
        if (textoVida != null) textoVida.text = "HEALTH: " + currentHealth.ToString();
    }
    private void UpdateFloatingHealthBar(int currentHealth) {
        if (healthBarSlider != null) healthBarSlider.value = (float)currentHealth / maxHealth;
        if (healthBarInstance != null) healthBarInstance.SetActive(currentHealth > 0 && !IsDead.Value);
    }

    // ============================================
    // --- SECCIÓN: Server RPCs (Cliente llama) ---
    // ============================================

    [ServerRpc]
    private void RequestInitialDataServerRpc(ServerRpcParams rpcParams = default)
    {
        ulong clientId = rpcParams.Receive.SenderClientId;
        Debug.Log($"Servidor: Recibida llamada  RequestInitialDataServerRpc de {clientId}. GameManager se encarga de la asignación inicial.");
    }

    [ServerRpc]
    void SubmitMovementRequestServerRpc(Vector3 direction, ServerRpcParams rpcParams = default)
    {
        if (IsDead.Value) return;
        transform.Translate(direction * moveSpeed * Time.deltaTime, Space.World);
    }

    
    [ServerRpc]
    void SubmitFireRequestServerRpc(Vector3 spawnPos, Quaternion spawnRot, ServerRpcParams rpcParams = default)
    {
        if (IsDead.Value) return; // Ignorar si muerto
        ulong clientId = rpcParams.Receive.SenderClientId;

        // Aquí puedes añadir una comprobación de cooldown más estricta en el servidor si es necesario

        if (bulletPrefab != null)
        {
            GameObject bulletInstance = Instantiate(bulletPrefab, spawnPos, spawnRot);
            NetworkObject bulletNetworkObject = bulletInstance.GetComponent<NetworkObject>();
            Bullet bulletScript = bulletInstance.GetComponent<Bullet>();

            if (bulletNetworkObject != null && bulletScript != null)
            {
                // Determinar el color actual del jugador (podría estar afectado por estado de muerte, aunque isDead ya se comprueba)
                Color jugadorColorActual = playerMeshRenderer != null ? playerMeshRenderer.material.color : NetworkColor.Value;
                if (IsDead.Value) jugadorColorActual = deadColor; // Asegurar que si dispara justo al morir (difícil), la bala sea gris

                // Inicializar la bala ANTES de hacerla visible en red
                bulletScript.Initialize(OwnerClientId, jugadorColorActual, bulletImpactEffectPrefab);

                // Hacer spawn de la bala en la red (el servidor se vuelve propietario)
                bulletNetworkObject.Spawn(true); // true para que se destruya si el servidor se detiene
                Debug.Log($"Servidor: Bala spawneada para {clientId}");
            }
            else
            {
                 Debug.LogError($"Prefab de bala ({bulletPrefab.name}) no tiene NetworkObject o Bullet script.");
                 Destroy(bulletInstance); // Limpiar instancia fallida
            }
        }
        else
        {
             Debug.LogError($"Player {clientId}: Prefab de bala no asignado en el Inspector.");
        }
    }
    // --- FIN LÓGICA DE DISPARO ---

    [ServerRpc]
    void SubmitInteractionRequestServerRpc(ServerRpcParams rpcParams = default)
    {
        if (IsDead.Value) return;
        Debug.Log($"Servidor: Jugador {OwnerClientId} intentó interactuar.");

        Collider[] hits = Physics.OverlapSphere(transform.position, interactionRadius, interactableLayer);
        GameObject closestInteractable = null;
        float minDistance = float.MaxValue;

        foreach (var hitCollider in hits)
        {
            Door door = hitCollider.GetComponent<Door>();
            if (door != null)
            {
                // Simplificación sin Raycast por ahora, si está en el radio y capa, interactúa
                float distance = Vector3.Distance(transform.position, hitCollider.transform.position);
                if (distance < minDistance) {
                    minDistance = distance;
                    closestInteractable = hitCollider.gameObject;
                }
                // Descomentar y ajustar bloque Raycast si se quiere visibilidad estricta
                /*
                 RaycastHit visibilityHit;
                 Vector3 directionToDoor = (hitCollider.transform.position - transform.position).normalized;
                 Vector3 rayOrigin = transform.position + Vector3.up * 0.5f; // Origen ligeramente elevado

                 if (Physics.Raycast(rayOrigin, directionToDoor, out visibilityHit, interactionRadius * 1.1f))
                 {
                     if (visibilityHit.collider == hitCollider) // ¿Realmente vemos la puerta?
                     {
                          if (distance < minDistance) { // 'distance' ya calculado arriba
                              minDistance = distance;
                              closestInteractable = hitCollider.gameObject;
                          }
                     } // else { Debug.Log($"Servidor: Raycast golpeó {visibilityHit.collider.name} en lugar de {hitCollider.name}"); }
                 } // else { Debug.Log($"Servidor: Raycast hacia {hitCollider.name} no golpeó nada."); }
                */
            }
        }

        if (closestInteractable != null)
        {
            Debug.Log($"Servidor: Jugador {OwnerClientId} interactuando con {closestInteractable.name}");
            Door doorToToggle = closestInteractable.GetComponent<Door>();
            if (doorToToggle != null) {
                doorToToggle.ToggleDoorState(); // Llama al método en Door.cs
            }
        } // else { Debug.Log($"Servidor: Jugador {OwnerClientId} no encontró nada con qué interactuar."); }
    }

    [ServerRpc]
    void SubmitDashRequestServerRpc(Vector3 direction)
    {
        if (IsDead.Value || isDashing) return;
        StartCoroutine(PerformDashServer(direction));
        NotifyDashClientRpc(direction);
    }

    // ===========================================
    // --- SECCIÓN: Client RPCs (Servidor llama) ---
    // ===========================================
    [ClientRpc]
    private void TeleportClientRpc(Vector3 position, ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;
        transform.position = position;
    }
    [ClientRpc]
    void NotifyDashClientRpc(Vector3 direction)
    {
        if (dashEffectPrefab != null) { GameObject fx = Instantiate(dashEffectPrefab, transform.position, Quaternion.LookRotation(direction)); Destroy(fx, 1.0f); }
    }

    // RPC para notificar al cliente propietario que recogió un powerup
    [ClientRpc]
    private void NotifyPowerUpPickupClientRpc(PowerUpType type, ClientRpcParams clientRpcParams = default)
    {
        Debug.Log($"Player (Cliente): Recibí notificación de recogida de {type}!");
        StartCoroutine(HandlePowerUpEffectLocal(type));
    }


    // ========================================
    // --- SECCIÓN: Lógica de Juego (Vida) ---
    // ========================================
    public void TakeDamage(int amount)
    {
        if (!IsServer || IsDead.Value || isDashing) return;
        int prev = NetworkSalud.Value;
        NetworkSalud.Value = Mathf.Max(0, NetworkSalud.Value - amount);
        if (NetworkSalud.Value <= 0 && prev > 0) Die();
    }

    private void Die()
    {
        if (!IsServer || IsDead.Value) return;
        IsDead.Value = true; // Sincroniza estado, dispara OnIsDeadChanged
        Debug.Log($"Servidor: Jugador {OwnerClientId} ha muerto.");
        // --- Corrección Muerte/Respawn ---
        // StartCoroutine(RespawnTimer(5.0f)); // No respawnear automáticamente
        // --- Fin Corrección ---
    }

    // Dejar RespawnTimer y Respawn por si se implementa un respawn manual más tarde
    private IEnumerator RespawnTimer(float delay) { yield return new WaitForSeconds(delay); Respawn(); }
    private void Respawn() {
        if (!IsServer) return;
        Debug.Log($"Servidor: Respawneando jugador {OwnerClientId}");
        Vector3 spawnPos = Vector3.up; // Default
        GameManager gm = GameManager.Instance; // Asume Singleton
        if (gm != null) spawnPos = gm.GetRandomSpawnPoint(); // Necesitas este método en GameManager
        transform.position = spawnPos;
        NetworkSalud.Value = maxHealth;
        IsDead.Value = false; // Esto dispara OnIsDeadChanged para revertir estado visual/físico
    }

    public void QuitarVida(int cantidad) { TakeDamage(cantidad); } // Para compatibilidad HUD


    // ===========================================
    // --- SECCIÓN: Lógica de Power-Ups ---
    // ===========================================
    public void ServerHandlePowerUpPickup(PowerUpType type)
    {
        if (!IsServer || IsDead.Value) return;
        ApplyPowerUpEffectOnServer(type);
        NotifyPowerUpPickupClientRpc(type, new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } } });
    }

    private void ApplyPowerUpEffectOnServer(PowerUpType type)
    {
        if (!IsServer) return;
        switch (type) {
            case PowerUpType.Speed: StartCoroutine(ServerPowerUpDuration(type, powerUpDuration)); break;
            case PowerUpType.FireRate: StartCoroutine(ServerPowerUpDuration(type, powerUpDuration)); break;
            case PowerUpType.Health: NetworkSalud.Value = Mathf.Min(maxHealth, NetworkSalud.Value + healthBoostAmount); break;
        }
    }

    private IEnumerator ServerPowerUpDuration(PowerUpType type, float duration)
    {
        float originalValue = 0; // Necesario declararlo fuera del switch si se usa después
        switch (type) {
            case PowerUpType.Speed: originalValue = moveSpeed; moveSpeed = baseMoveSpeed * speedBoostMultiplier; break;
            case PowerUpType.FireRate: originalValue = fireRate; fireRate = baseFireRate / fireRateBoostMultiplier; break;
        }
        Debug.Log($"Player (Servidor): Efecto {type} activado para {OwnerClientId}.");
        yield return new WaitForSeconds(duration);
        switch (type) { // Revertir usando valores base
            case PowerUpType.Speed: moveSpeed = baseMoveSpeed; break;
            case PowerUpType.FireRate: fireRate = baseFireRate; break;
        }
         Debug.Log($"Player (Servidor): Efecto {type} expirado para {OwnerClientId}.");
    }

    private IEnumerator HandlePowerUpEffectLocal(PowerUpType type)
    {
        GameObject visualEffectInstance = null; float currentDuration = powerUpDuration;
        switch (type) {
            case PowerUpType.Speed: if (speedEffectVisual != null) speedEffectVisual.SetActive(true); visualEffectInstance = speedEffectVisual; break;
            case PowerUpType.FireRate: if (fireRateEffectVisual != null) fireRateEffectVisual.SetActive(true); visualEffectInstance = fireRateEffectVisual; break;
            case PowerUpType.Health: currentDuration = 0; break; // Instantáneo
        }
        ActivePowerUp newActivePowerUp = new ActivePowerUp { Type = type, VisualEffect = visualEffectInstance };
        if (currentDuration > 0 || visualEffectInstance != null) {
            Coroutine expiry = StartCoroutine(RevertPowerUpEffectLocal(type, currentDuration, newActivePowerUp));
            newActivePowerUp.ExpiryCoroutine = expiry; activePowerUps.Add(newActivePowerUp);
        }
        yield return null;
    }

    private IEnumerator RevertPowerUpEffectLocal(PowerUpType type, float duration, ActivePowerUp trackingInfo)
    {
        if (duration > 0) yield return new WaitForSeconds(duration);
        if (trackingInfo.VisualEffect != null) trackingInfo.VisualEffect.SetActive(false);
        activePowerUps.Remove(trackingInfo);
    }

     private void StopAllPowerupCoroutinesAndVisuals() {
         if (!IsOwner) return;
         foreach(var powerup in activePowerUps) {
             if (powerup.VisualEffect != null) powerup.VisualEffect.SetActive(false);
             if (powerup.ExpiryCoroutine != null) StopCoroutine(powerup.ExpiryCoroutine);
         }
         activePowerUps.Clear();
     }


    // =====================================================
    // --- SECCIÓN: Lógica de Mecánica Nueva (Dash Server) ---
    // =====================================================
    private IEnumerator PerformDashServer(Vector3 direction) {
         if (!IsServer) yield break;
         isDashing = true;
         bool originalGravity = rb.useGravity; rb.useGravity = false;
         float elapsedTime = 0f; Vector3 startPos = transform.position; Vector3 targetPos = transform.position + direction * dashDistance;
         RaycastHit hit; if (Physics.Raycast(startPos, direction, out hit, dashDistance)) targetPos = hit.point - direction * 0.1f; // Ajustar posición si choca
         Vector3 velocity = direction * (dashDistance / dashDuration); // Calcular velocidad constante para el dash

         while (elapsedTime < dashDuration) {
            // Mover usando Rigidbody si quieres interacción física durante el dash, o Translate si quieres movimiento más directo.
            // rb.velocity = velocity; // Opción física
            transform.Translate(velocity * Time.deltaTime, Space.World); // Opción directa

            // Salir si choca durante el movimiento (opcional, si no se usó raycast antes)
            // RaycastHit midDashHit;
            // if(Physics.Raycast(transform.position, direction, out midDashHit, velocity.magnitude * Time.deltaTime * 1.1f)) {
            //     transform.position = midDashHit.point - direction * 0.1f;
            //     break; // Detener dash
            // }

            elapsedTime += Time.deltaTime;
            yield return null;
         }

         rb.linearVelocity = Vector3.zero; // Detener movimiento al final
         rb.useGravity = originalGravity;
         isDashing = false;
    }
}
