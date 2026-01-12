using UnityEngine;

public class DebugRacketCollisionLogger : MonoBehaviour
{
    private void OnCollisionEnter(Collision collision)
    {
        Debug.Log($"[DEBUGRACKETCOLLIDER] OnCollisionEnter with: {collision.gameObject.name}, tag: {collision.gameObject.tag}, layer: {collision.gameObject.layer}");
    }

    private void OnTriggerEnter(Collider other)
    {
        Debug.Log($"[DEBUGRACKETCOLLIDER] OnTriggerEnter with: {other.gameObject.name}, tag: {other.gameObject.tag}, layer: {other.gameObject.layer}");
    }
}