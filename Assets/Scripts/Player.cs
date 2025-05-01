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

    // --- Propiedades Públicas (Getters) --- NEW/VERIFIED
    public int MaxHealth => maxHealth;
    public int HealthBoostAmount => healthBoostAmount;
    // public float PowerUpDuration => powerUpDuration; // Si otros scripts necesitan saber la duración

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
        // Debug.Log($"Player Spawned. Owner: {OwnerClientId}, IsOwner: {IsOwner}, IsServer: {IsServer}");
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
            nextFireTime = Time.time + fireRate;
            SubmitFireRequestServerRpc(firePoint.position, firePoint.rotation);
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
    // (SetupLocalUI, SetupFloatingHealthBar, VerifyNetworkTransformConfig - Sin cambios)
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
    // (OnHealthChanged, ColorChanged, OnIsDeadChanged - Sin cambios relevantes, solo limpieza menor)
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

        GetComponent<Collider>().enabled = !newValue;
        if (playerMeshRenderer != null) playerMeshRenderer.enabled = !newValue || IsOwner;
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
        }
    }

    // ===========================================
    // --- SECCIÓN: Actualización de UI / Visual ---
    // ===========================================
    // (UpdateLocalHealthUI, UpdateFloatingHealthBar - Sin cambios)
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
    // (RequestInitialDataServerRpc, SubmitMovementRequestServerRpc, SubmitFireRequestServerRpc, SubmitInteractionRequestServerRpc, SubmitDashRequestServerRpc - Sin cambios funcionales)
    [ServerRpc] private void RequestInitialDataServerRpc(ServerRpcParams rpcParams = default) { /* ... requiere GameManager ... */ }
    [ServerRpc] void SubmitMovementRequestServerRpc(Vector3 direction, ServerRpcParams rpcParams = default) { if (IsDead.Value) return; transform.Translate(direction * moveSpeed * Time.deltaTime, Space.World); }
    [ServerRpc] void SubmitFireRequestServerRpc(Vector3 spawnPos, Quaternion spawnRot, ServerRpcParams rpcParams = default) { if (IsDead.Value) return; /* ... requiere Bullet.cs ... */ }
    [ServerRpc] void SubmitInteractionRequestServerRpc(ServerRpcParams rpcParams = default) { if (IsDead.Value) return; /* ... requiere Door.cs ... */ }
    [ServerRpc] void SubmitDashRequestServerRpc(Vector3 direction) { if (IsDead.Value || isDashing) return; StartCoroutine(PerformDashServer(direction)); NotifyDashClientRpc(direction); }


    // ===========================================
    // --- SECCIÓN: Client RPCs (Servidor llama) ---
    // ===========================================
    // (TeleportClientRpc, NotifyDashClientRpc - Sin cambios)
    // (NotifyPowerUpPickupClientRpc - Movido a Lógica PowerUp)
    [ClientRpc] private void TeleportClientRpc(Vector3 position, ClientRpcParams rpcParams = default) { if (!IsOwner) return; transform.position = position; }
    [ClientRpc] void NotifyDashClientRpc(Vector3 direction) { if (dashEffectPrefab != null) { GameObject fx = Instantiate(dashEffectPrefab, transform.position, Quaternion.LookRotation(direction)); Destroy(fx, 1.0f); } }

    // ========================================
    // --- SECCIÓN: Lógica de Juego (Vida) ---
    // ========================================
    // (TakeDamage, Die, RespawnTimer, Respawn, QuitarVida - Sin cambios funcionales)
    public void TakeDamage(int amount) { if (!IsServer || IsDead.Value || isDashing) return; int prev = NetworkSalud.Value; NetworkSalud.Value = Mathf.Max(0, NetworkSalud.Value - amount); if (NetworkSalud.Value <= 0 && prev > 0) Die(); }
    private void Die() { if (!IsServer || IsDead.Value) return; IsDead.Value = true; /* ... lógica server muerte ... */ StartCoroutine(RespawnTimer(5.0f)); } // Ejemplo respawn
    private IEnumerator RespawnTimer(float delay) { yield return new WaitForSeconds(delay); Respawn(); }
    private void Respawn() { if (!IsServer) return; Vector3 spawnPos = Vector3.up; /* ... obtener spawn de GameManager ... */ transform.position = spawnPos; NetworkSalud.Value = maxHealth; IsDead.Value = false; }
    public void QuitarVida(int cantidad) { TakeDamage(cantidad); } // Para compatibilidad HUD

    // ===========================================
    // --- SECCIÓN: Lógica de Power-Ups ---
    // ===========================================

    // --- MÉTODO PÚBLICO LLAMADO POR PowerUp.cs EN EL SERVIDOR --- NEW
    public void ServerHandlePowerUpPickup(PowerUpType type)
    {
        if (!IsServer || IsDead.Value) return; // Solo servidor y si no está muerto

        Debug.Log($"Player (Servidor): Procesando recogida de {type} para {OwnerClientId}");
        // 1. Aplicar efecto lógico en el servidor (incluye corutina si dura)
        ApplyPowerUpEffectOnServer(type);
        // 2. Notificar SOLO al cliente propietario para efectos locales y feedback
        NotifyPowerUpPickupClientRpc(type, new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } } });
    }

    // Aplica la lógica del powerup EN EL SERVIDOR
    private void ApplyPowerUpEffectOnServer(PowerUpType type)
    {
        if (!IsServer) return;

        switch (type)
        {
            case PowerUpType.Speed:
                StartCoroutine(ServerPowerUpDuration(type, powerUpDuration));
                break;
            case PowerUpType.FireRate:
                StartCoroutine(ServerPowerUpDuration(type, powerUpDuration));
                break;
            case PowerUpType.Health:
                // Accede a sus propias variables miembro directamente
                NetworkSalud.Value = Mathf.Min(maxHealth, NetworkSalud.Value + healthBoostAmount);
                break;
        }
    }

    // Corutina del SERVIDOR para manejar la duración de Speed/FireRate
    private IEnumerator ServerPowerUpDuration(PowerUpType type, float duration)
    {
        // Aplicar efecto en servidor
        float originalValue = 0;
        switch (type)
        {
            case PowerUpType.Speed: originalValue = moveSpeed; moveSpeed = baseMoveSpeed * speedBoostMultiplier; break;
            case PowerUpType.FireRate: originalValue = fireRate; fireRate = baseFireRate / fireRateBoostMultiplier; break;
        }
        Debug.Log($"Player (Servidor): Efecto {type} activado para {OwnerClientId}.");

        yield return new WaitForSeconds(duration);

        // Revertir efecto en servidor
        switch (type)
        {
            case PowerUpType.Speed: moveSpeed = baseMoveSpeed; break; // Revertir a base
            case PowerUpType.FireRate: fireRate = baseFireRate; break; // Revertir a base
        }
         Debug.Log($"Player (Servidor): Efecto {type} expirado para {OwnerClientId}.");
    }

    // RPC para notificar al cliente propietario que recogió un powerup
    [ClientRpc]
    private void NotifyPowerUpPickupClientRpc(PowerUpType type, ClientRpcParams clientRpcParams = default)
    {
        // El RPC se envía solo al cliente target, así que no necesitamos comprobar IsOwner aquí explícitamente
        Debug.Log($"Player (Cliente): Recibí notificación de recogida de {type}!");
        // Iniciar efectos locales (visuales, UI, sonido, corutina de duración local)
        StartCoroutine(HandlePowerUpEffectLocal(type));
    }

    // Corutina del CLIENTE para manejar efectos locales (visuales, stats locales)
    private IEnumerator HandlePowerUpEffectLocal(PowerUpType type)
    {
        Debug.Log($"Player (Cliente): Aplicando efecto local para {type}");
        GameObject visualEffectInstance = null;
        float currentDuration = powerUpDuration;

        // Aplicar efecto VISUAL y de STATS localmente (para feedback inmediato)
        switch (type)
        {
            case PowerUpType.Speed:
                // moveSpeed = baseMoveSpeed * speedBoostMultiplier; // El servidor controla el movimiento real
                if (speedEffectVisual != null) speedEffectVisual.SetActive(true);
                visualEffectInstance = speedEffectVisual;
                break;
            case PowerUpType.FireRate:
                // fireRate = baseFireRate / fireRateBoostMultiplier; // El servidor controla cooldown real
                 if (fireRateEffectVisual != null) fireRateEffectVisual.SetActive(true);
                 visualEffectInstance = fireRateEffectVisual;
                break;
            case PowerUpType.Health:
                 // El valor ya se actualizó vía NetworkVariable. Mostrar efecto instantáneo si hay.
                 Debug.Log("Player (Cliente): Efecto de vida ya aplicado por NetworkVariable.");
                 currentDuration = 0; // No necesita corutina de duración para el stat
                break;
        }

         // Añadir a la lista de activos para revertir LUEGO VISUALES locales
         ActivePowerUp newActivePowerUp = new ActivePowerUp { Type = type, VisualEffect = visualEffectInstance };
         // Solo iniciar corrutina de expiración si hay duración y/o efecto visual
         if (currentDuration > 0 || visualEffectInstance != null)
         {
             Coroutine expiry = StartCoroutine(RevertPowerUpEffectLocal(type, currentDuration, newActivePowerUp));
             newActivePowerUp.ExpiryCoroutine = expiry;
             activePowerUps.Add(newActivePowerUp);
         }

        yield return null; // Necesario si no hubo yield break antes
    }

    // Corutina del CLIENTE para revertir efectos VISUALES locales
    private IEnumerator RevertPowerUpEffectLocal(PowerUpType type, float duration, ActivePowerUp trackingInfo)
    {
        if (duration > 0) yield return new WaitForSeconds(duration);

        Debug.Log($"Player (Cliente): Revirtiendo efecto local para {type}");
        // Revertir STATS locales (generalmente no necesario ya que el servidor manda)
        // Revertir efecto VISUAL local
        if (trackingInfo.VisualEffect != null) trackingInfo.VisualEffect.SetActive(false);

        activePowerUps.Remove(trackingInfo);
    }

     // Detiene corutinas y efectos visuales locales al morir, etc.
     private void StopAllPowerupCoroutinesAndVisuals() {
         if (!IsOwner) return; // Solo el propietario gestiona esto localmente
         foreach(var powerup in activePowerUps) {
             if (powerup.VisualEffect != null) powerup.VisualEffect.SetActive(false);
             if (powerup.ExpiryCoroutine != null) StopCoroutine(powerup.ExpiryCoroutine);
         }
         activePowerUps.Clear();
     }

    // =====================================================
    // --- SECCIÓN: Lógica de Mecánica Nueva (Dash Server) ---
    // =====================================================
    // (PerformDashServer - Sin cambios funcionales)
    private IEnumerator PerformDashServer(Vector3 direction) {
         if (!IsServer) yield break;
         isDashing = true;
         bool originalGravity = rb.useGravity; rb.useGravity = false;
         float elapsedTime = 0f; Vector3 startPos = transform.position; Vector3 targetPos = transform.position + direction * dashDistance;
         RaycastHit hit; if (Physics.Raycast(startPos, direction, out hit, dashDistance)) targetPos = hit.point - direction * 0.1f;
         while (elapsedTime < dashDuration) { transform.Translate(direction * (dashDistance / dashDuration) * Time.deltaTime, Space.World); elapsedTime += Time.deltaTime; yield return null; }
         rb.linearVelocity = Vector3.zero; rb.useGravity = originalGravity; isDashing = false;
    }
}
