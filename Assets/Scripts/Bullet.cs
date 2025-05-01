using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components;
using System.Collections; // Necesario para Coroutine

[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(Collider))]
[RequireComponent(typeof(NetworkRigidbody))] // O NetworkTransform si prefieres
public class Bullet : NetworkBehaviour
{
    [Header("Configuración")]
    [SerializeField] private float speed = 50f; // Ajusta según necesidad
    [SerializeField] private int damage = 10;
    [SerializeField] private float lifeTime = 5.0f;

    // Variables establecidas por el servidor antes de Spawn()
    private ulong ownerClientId;
    private Color bulletColor;
    private GameObject impactEffectPrefab;

    // Componentes cacheados
    private Rigidbody rb;
    private Renderer bulletRenderer;

    // Estado interno
    private bool hitDetected = false;

    // Llamado por Player.cs en el servidor DESPUÉS de Instantiate, ANTES de Spawn()
    public void Initialize(ulong ownerId, Color color, GameObject impactFxPrefab)
    {
        // Solo guarda datos
        this.ownerClientId = ownerId;
        this.bulletColor = color;
        this.impactEffectPrefab = impactFxPrefab;

        // Aplicar color visual
        bulletRenderer = GetComponent<Renderer>();
        if (bulletRenderer != null) {
           bulletRenderer.material.color = this.bulletColor;
        }
    }

    public override void OnNetworkSpawn()
    {
        // Obtener componentes
        rb = GetComponent<Rigidbody>();
        if (rb == null) {
             Debug.LogError($"Bala {NetworkObjectId}: Rigidbody no encontrado!");
             if(IsServer) NetworkObject.Despawn(true); // Limpiar si falta componente esencial
             return;
        }

        // Aplicar color (redundante por si acaso)
        if (bulletRenderer == null) bulletRenderer = GetComponent<Renderer>();
        if (bulletRenderer != null) bulletRenderer.material.color = bulletColor;

        // --- Lógica Específica del Servidor ---
        if (IsServer)
        {
            // Autodestrucción programada
            Invoke(nameof(DestroySelf), lifeTime);

            // --- ¡¡INICIAR COROUTINE PARA APLICAR VELOCIDAD!! ---
            // No aplicar velocidad directamente aquí, esperar un frame.
            StartCoroutine(ApplyInitialVelocityAfterSpawn());
            // --- FIN INICIO COROUTINE ---

            // Log informativo (opcional)
            Debug.Log($"Bala Servidor {NetworkObjectId} (OnNetworkSpawn): Iniciando coroutine para aplicar velocidad. IsKinematic AHORA = {rb.isKinematic}");
        }
        // --- Lógica Específica del Cliente ---
        else // (!IsServer)
        {
             // Log informativo (opcional)
             if (rb != null) Debug.Log($"Bala Cliente {NetworkObjectId} (OnNetworkSpawn): IsKinematic={rb.isKinematic}"); // Debería ser True
        }
    }

    // --- COROUTINE PARA APLICAR VELOCIDAD EN EL SERVIDOR ---
    private IEnumerator ApplyInitialVelocityAfterSpawn()
    {
        // Esperar al final del frame actual o al inicio del siguiente frame.
        // Esto da tiempo a NetworkRigidbody a establecer isKinematic = false en el servidor.
        yield return null; // Esperar un frame

        // Doble chequeo por si el objeto fue destruido o ya no somos servidor
        if (!IsServer || rb == null || NetworkObject == null || !NetworkObject.IsSpawned)
        {
            yield break; // Salir de la coroutine si algo cambió
        }

        // ¡AHORA debería ser seguro aplicar la velocidad!
        if (!rb.isKinematic) // Comprobación final
        {
            Vector3 initialVelocity = transform.forward * speed;
            rb.linearVelocity = initialVelocity;
            Debug.Log($"Bala Servidor {NetworkObjectId} (ApplyVelocityAfterSpawn): Velocidad aplicada = {initialVelocity.magnitude} ({initialVelocity}). IsKinematic={rb.isKinematic}");
        }
        else
        {
             // Si sigue siendo Kinematic aquí, hay un problema más profundo con NetworkRigidbody o la configuración.
             Debug.LogError($"Bala Servidor {NetworkObjectId} (ApplyVelocityAfterSpawn): ¡¡Rigidbody SIGUE SIENDO Kinematic después de esperar un frame!!");
        }
    }
    // --- FIN COROUTINE ---


    // --- Manejo de Colisiones (Principalmente en Servidor) ---
    private void OnCollisionEnter(Collision collision) {
        if (!IsServer || hitDetected) return;
        HandleCollision(collision.gameObject, collision.contacts[0].point, collision.contacts[0].normal);
    }
     private void OnTriggerEnter(Collider other) {
         if (!IsServer || hitDetected) return;
         HandleCollision(other.gameObject, other.ClosestPoint(transform.position), (transform.position - other.transform.position).normalized);
     }
    private void HandleCollision(GameObject hitObject, Vector3 hitPoint, Vector3 hitNormal) {
        if (!IsServer || hitDetected) return;
        hitDetected = true;
        Player hitPlayer = hitObject.GetComponent<Player>();
        if (hitPlayer != null && hitPlayer.OwnerClientId != ownerClientId) {
            hitPlayer.TakeDamage(damage);
        }
        SpawnImpactEffectClientRpc(hitPoint, hitNormal);
        DestroySelf();
    }
    private void DestroySelf() {
        if (!IsServer) return;
        CancelInvoke(nameof(DestroySelf));
        if (NetworkObject != null && NetworkObject.IsSpawned) {
            NetworkObject.Despawn(true);
        }
    }

    // --- Client RPC para Efecto Visual ---
    [ClientRpc]
    private void SpawnImpactEffectClientRpc(Vector3 position, Vector3 normal) {
        if (IsServer) return;
        if (impactEffectPrefab != null) {
            GameObject effect = Instantiate(impactEffectPrefab, position, Quaternion.LookRotation(normal));
            Destroy(effect, 2.0f);
        }
    }
}
