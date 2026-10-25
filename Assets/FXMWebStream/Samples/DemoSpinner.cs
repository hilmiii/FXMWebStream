using UnityEngine;

namespace FXM.WebStream.Samples
{
    /// <summary>
    /// Small sample for loopback test scenes: rotates an object so frame changes are
    /// visible on the receiver. Not part of the streaming runtime.
    /// </summary>
    [AddComponentMenu("FXM WebStream/Samples/Demo Spinner")]
    public sealed class DemoSpinner : MonoBehaviour
    {
        public Vector3 degreesPerSecond = new Vector3(0f, 60f, 0f);

        private void Update()
        {
            transform.Rotate(degreesPerSecond * Time.deltaTime, Space.World);
        }
    }
}
