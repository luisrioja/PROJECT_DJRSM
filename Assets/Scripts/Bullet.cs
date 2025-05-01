using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components; // Si usas NetworkRigidbody/Transform

[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(Collider))]
// Aseg�rate de tener NetworkRigidbody O NetworkTransform, no ambos. NetworkRigidbody es mejor para f�sicas puras del servidor.
[RequireComponent(typeof(NetworkRigidbody))]
public class Bullet : NetworkBehaviour
{
    [SerializeField] private float speed = 20f;
    [SerializeField] private int damage = 10;
    [SerializeField] private float lifeTime = 5.0f; // Tiempo antes de autodestruirse si no choca

    // Informaci�n establecida por el servidor ANTES de hacer Spawn()
    private ulong ownerClientId;
    private Color bulletColor;
    private GameObject impactEffectPrefab; // Pasado desde el jugador

    private Rigidbody rb;
    private bool hitDetected = false; // Para evitar m�ltiples colisiones/efectos

    public override void OnNetworkSpawn()
    {
        rb = GetComponent<Rigidbody>();
        // Importante: El Collider debe ser trigger en los clientes si el Rigidbody es Kinematic
        // Collider col = GetComponent<Collider>();
        // if (!IsServer && col != null) col.isTrigger = true; // O manejar colisiones solo en servidor

        // Aplicar color (ya que NetworkVariable no se usa aqu� para el color)
        GetComponent<Renderer>().material.color = bulletColor;

        // Autodestrucci�n despu�s de lifeTime (solo el servidor necesita destruirlo en red)
        if (IsServer)
        {
            Invoke(nameof(DestroySelf), lifeTime);
        }

        // Si usamos NetworkRigidbody, la velocidad se aplica en Initialize y se sincroniza.
        // Si us�ramos NetworkTransform, tendr�amos que moverlo en Update() en el servidor.
    }

    // Llamado por Player.cs en el servidor DESPU�S de Instantiate, ANTES de Spawn()
    public void Initialize(ulong ownerId, Color color, GameObject impactFxPrefab)
    {
        ownerClientId = ownerId;
        bulletColor = color;
        impactEffectPrefab = impactFxPrefab;

        // Aplicar velocidad inicial (NetworkRigidbody la sincronizar�)
        GetComponent<Rigidbody>().linearVelocity = transform.forward * speed;

         // Aplicar color inmediatamente (antes de spawn)
         var renderer = GetComponent<Renderer>();
         if (renderer != null) {
            renderer.material.color = bulletColor;
         }
    }

    private void OnCollisionEnter(Collision collision)
    {
        // La l�gica de colisi�n REAL solo debe ejecutarse en el servidor
        if (!IsServer || hitDetected) return;

        HandleCollision(collision.gameObject, collision.contacts[0].point, collision.contacts[0].normal);
    }

     private void OnTriggerEnter(Collider other) {
         // OnTriggerEnter puede ser �til si los colliders de los jugadores son triggers
         // o si la bala es un trigger en los clientes.
         if (!IsServer || hitDetected) return;

         // Simplificaci�n: Asumimos que el punto de impacto es el centro del otro collider
         HandleCollision(other.gameObject, other.transform.position, (transform.position - other.transform.position).normalized);
     }

    private void HandleCollision(GameObject hitObject, Vector3 hitPoint, Vector3 hitNormal)
    {
        if (!IsServer || hitDetected) return; // Doble chequeo

        hitDetected = true; // Marcar que ya hemos colisionado

        Debug.Log($"Bala (Servidor): Colisi�n detectada con {hitObject.name}");

        // Intentar obtener el componente Player del objeto golpeado
        Player hitPlayer = hitObject.GetComponent<Player>();

        // Comprobar si es un jugador y NO es el propietario de la bala
        if (hitPlayer != null && hitPlayer.OwnerClientId != ownerClientId)
        {
            Debug.Log($"Bala (Servidor): Golpe� al jugador {hitPlayer.OwnerClientId}. Aplicando da�o.");
            // Aplicar da�o al jugador (el m�todo TakeDamage ya verifica si es servidor)
            hitPlayer.TakeDamage(damage);
        }
        else if (hitPlayer != null && hitPlayer.OwnerClientId == ownerClientId)
        {
            Debug.Log($"Bala (Servidor): Golpe� al propio due�o ({ownerClientId}). Ignorando da�o.");
            // No hacer nada si choca con el due�o (o aplicar l�gica diferente si se quiere)
        }

        // Notificar a los clientes para que creen el efecto de impacto
        SpawnImpactEffectClientRpc(hitPoint, hitNormal);

        // Destruir la bala en la red
        DestroySelf();
    }

    private void DestroySelf()
    {
        if (!IsServer) return; // Solo el servidor destruye

        // Cancelar Invoke si se destruye antes por colisi�n
        CancelInvoke(nameof(DestroySelf));

        if (NetworkObject != null && NetworkObject.IsSpawned)
        {
            Debug.Log("Bala (Servidor): Despawneando objeto.");
            NetworkObject.Despawn(true); // true para destruir el objeto en todos los clientes
        }
        else
        {
            Debug.LogWarning("Bala (Servidor): Intentando destruir pero no est� spawneada o no tiene NetworkObject.");
            // Destroy(gameObject); // Destrucci�n local si algo fall�
        }
    }

    [ClientRpc]
    private void SpawnImpactEffectClientRpc(Vector3 position, Vector3 normal)
    {
        // Crear efecto visual SOLO en los clientes
        if (IsServer) return;

        Debug.Log("Bala (Cliente): Creando efecto de impacto visual.");
        if (impactEffectPrefab != null)
        {
            GameObject effect = Instantiate(impactEffectPrefab, position, Quaternion.LookRotation(normal));
            // Destruir el efecto despu�s de un tiempo
            Destroy(effect, 2.0f);
        }
        else
        {
            Debug.LogWarning("Bala (Cliente): Falta el prefab del efecto de impacto.");
        }
    }
}
