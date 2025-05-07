using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components; 
using System.Collections; 

// Componentes requeridos para el correcto funcionamiento de la bala.
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(Collider))]
[RequireComponent(typeof(NetworkRigidbody))] 
public class Bullet : NetworkBehaviour 
{
    [Header("Configuración")]
    [SerializeField] private float speed = 50f; // Velocidad de la bala.
    [SerializeField] private int damage = 10; // Daño que inflige la bala.
    [SerializeField] private float lifeTime = 5.0f; // Tiempo en segundos antes de autodestruirse.

    // Variables establecidas por el servidor antes de que la bala sea spawneada en la red.
    private ulong ownerClientId; // ID del cliente que disparó la bala.
    private Color bulletColor; // Color de la bala (puede coincidir con el del jugador).
    // La referencia al prefab de impacto ya no se almacena/usa aquí si VFXManager lo gestiona.
    // private GameObject impactEffectPrefab;

    // Componentes cacheados para acceso rápido.
    private Rigidbody rb; // Componente Rigidbody de la bala.
    private Renderer bulletRenderer; // Renderer para cambiar el color de la bala.

    // Estado interno de la bala.
    private bool hitDetected = false; // Flag para asegurar que una colisión se procese solo una vez.

    // Método llamado por Player.cs en el servidor DESPUÉS de instanciar la bala y ANTES de spawnearla.
    // Se usa para pasar datos iniciales desde el que dispara.
    public void Initialize(ulong ownerId, Color color, GameObject unusedImpactFxPrefab) // El parámetro impactFxPrefab ahora es 'unused'.
    {
        this.ownerClientId = ownerId;
        this.bulletColor = color;
        // this.impactEffectPrefab = unusedImpactFxPrefab; // Ya no se almacena esta referencia aquí.

        // Aplicar color visual a la bala.
        bulletRenderer = GetComponent<Renderer>();
        if (bulletRenderer != null) {
            bulletRenderer.material.color = this.bulletColor;
        }
    }

    // Se llama cuando el NetworkObject de la bala es spawneado en la red.
    public override void OnNetworkSpawn()
    {
        // Obtener componente Rigidbody.
        rb = GetComponent<Rigidbody>();
        if (rb == null) { // Seguridad: si no hay Rigidbody, la bala no puede funcionar.
            Debug.LogError($"Bala {NetworkObjectId}: Rigidbody no encontrado!");
            if(IsServer) NetworkObject.Despawn(true); // Despawnear si falta un componente esencial (solo en servidor).
            return;
        }

        // Re-aplicar color (seguridad, aunque Initialize ya lo hace).
        if (bulletRenderer == null) bulletRenderer = GetComponent<Renderer>();
        if (bulletRenderer != null) bulletRenderer.material.color = bulletColor;

        // Lógica específica del servidor.
        if (IsServer)
        {
            // Programar autodestrucción de la bala después de 'lifeTime' segundos.
            Invoke(nameof(DestroySelf), lifeTime);
            // Iniciar corutina para aplicar velocidad inicial (para evitar problemas con NetworkRigidbody).
            StartCoroutine(ApplyInitialVelocityAfterSpawn());
        }
        // En los clientes, el movimiento es manejado por la sincronización de NetworkRigidbody.
    }

    // Corutina para aplicar la velocidad inicial en el servidor después de un frame.
    // Esto da tiempo a NetworkRigidbody a inicializarse y asegurar que el Rigidbody no sea kinemático.
    private IEnumerator ApplyInitialVelocityAfterSpawn()
    {
        yield return null; // Esperar un frame.

        // Comprobaciones de seguridad por si el estado cambió durante la espera.
        if (!IsServer || rb == null || NetworkObject == null || !NetworkObject.IsSpawned)
        {
            yield break; // Salir de la corutina.
        }

        // Aplicar velocidad lineal si el Rigidbody no es kinemático.
        if (!rb.isKinematic)
        {
            Vector3 initialVelocity = transform.forward * speed; // Calcular velocidad en la dirección de la bala.
            rb.linearVelocity = initialVelocity; // Establecer velocidad.
        }
        else // Si sigue siendo kinemático, podría haber un problema de configuración.
        {
            Debug.LogWarning($"Bala Servidor {NetworkObjectId} (ApplyVelocityAfterSpawn): Rigidbody sigue siendo Kinematic.");
        }
    }

    // --- Manejo de Colisiones (Principalmente en Servidor) ---
    // Se llama cuando este Collider/Rigidbody comienza a tocar otro Rigidbody/Collider.
    private void OnCollisionEnter(Collision collision) {
        // Procesar colisión solo en el servidor y si no se ha detectado un impacto antes.
        if (!IsServer || hitDetected) return;
        HandleCollision(collision.gameObject, collision.contacts[0].point, collision.contacts[0].normal);
    }

    // Se llama cuando este Collider (marcado como Trigger) entra en contacto con otro Collider.
    private void OnTriggerEnter(Collider other) {
        // Procesar trigger solo en el servidor y si no se ha detectado un impacto antes.
        if (!IsServer || hitDetected) return;
        // Para Triggers, el punto de impacto y la normal se calculan de forma aproximada.
        HandleCollision(other.gameObject, other.ClosestPoint(transform.position), (transform.position - other.transform.position).normalized);
    }

    // Lógica centralizada para manejar un impacto (colisión o trigger). Ejecutado en el servidor.
    private void HandleCollision(GameObject hitObject, Vector3 hitPoint, Vector3 hitNormal) {
        if (!IsServer || hitDetected) return; // Doble chequeo de seguridad.
        hitDetected = true; // Marcar que ya se procesó un impacto para evitar múltiples efectos/daños.

        // Intentar aplicar daño si el objeto golpeado es un jugador y no es el que disparó la bala.
        Player hitPlayer = hitObject.GetComponent<Player>();
        if (hitPlayer != null && hitPlayer.OwnerClientId != ownerClientId) {
            hitPlayer.TakeDamage(damage); // Llamar al método TakeDamage del jugador.
        }

        // --- LLAMADA AL VFXMANAGER PARA EFECTO DE IMPACTO ---
        // El servidor solicita al VFXManager que reproduzca el efecto de impacto en todos los clientes.
        if (VFXManager.Instance != null)
        {
            VFXManager.Instance.PlayBulletImpactClientRpc(hitPoint, hitNormal); // Pasar posición y normal del impacto.
        }
        else
        {
            Debug.LogError("Bullet: VFXManager.Instance no encontrado. No se puede mostrar el efecto de impacto.", this);
        }
        // --- FIN LLAMADA VFXMANAGER ---

        DestroySelf(); // Iniciar el proceso de destrucción de la bala después del impacto.
    }

    // Método para destruir la bala. Se ejecuta solo en el servidor.
    private void DestroySelf() {
        if (!IsServer) return; // Solo el servidor puede destruir NetworkObjects.

        // Cancelar la llamada a Invoke(nameof(DestroySelf)) si este método fue llamado por una colisión.
        CancelInvoke(nameof(DestroySelf));

        // Si la bala se autodestruye por tiempo (no por una colisión previa).
        if (!hitDetected)
        {
            // --- LLAMADA AL VFXMANAGER PARA EFECTO DE IMPACTO (AL AUTODESTRUIRSE) ---
            // Mostrar un efecto en la última posición de la bala.
            if (VFXManager.Instance != null)
            {
                VFXManager.Instance.PlayBulletImpactClientRpc(transform.position, transform.forward); // Usar posición y dirección actual.
            }
            else
            {
                Debug.LogError("Bullet: VFXManager.Instance no encontrado. No se puede mostrar el efecto de impacto al autodestruirse.", this);
            }
            // --- FIN LLAMADA VFXMANAGER ---
        }

        // Despawnear el NetworkObject (esto lo destruirá en todos los clientes).
        if (NetworkObject != null && NetworkObject.IsSpawned) {
            NetworkObject.Despawn(true); // true = destruir el GameObject asociado.
        }
    }


}
