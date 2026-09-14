using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// Gives a harvestable node its own size.
    ///
    /// A spawn area places its nodes at a position and a rotation, and takes their size
    /// from the prefab — so a wood grown out of spawned entities is a wood in which every
    /// oak is exactly as tall as every other oak, which reads as a copied object rather
    /// than as trees. The scatter this replaced varied the scale of every rock and tree it
    /// put down, and losing that was the one thing the entities were visibly worse at.
    ///
    /// The size is worked out from where the node stands rather than randomed, because
    /// every client builds its own copy of the world: a random number would give the same
    /// tree a different height on each machine, and a player's landmark would not be their
    /// friend's. Position is the one thing all of them agree on, so hashing it gives every
    /// client the same answer without a byte crossing the network.
    ///
    /// It scales the whole entity, collider included, so what the player swings at stays
    /// the size of what they can see.
    /// </summary>
    public class DemoNodeVariety : MonoBehaviour
    {
        [Tooltip("Smallest and largest this node may come out, as a multiple of the model.")]
        public float smallest = 0.85f;
        public float largest = 1.25f;

        private void Start()
        {
            transform.localScale = Vector3.one * Mathf.Lerp(smallest, largest, FromPosition(transform.position));
        }

        /// <summary>
        /// A number between 0 and 1 that depends only on where a thing is standing.
        ///
        /// Rounded to a tenth of a metre first: a spawn position that differs in the last
        /// decimal place between a server and a client would otherwise hash to something
        /// else entirely and the node would change size as it was replicated.
        /// </summary>
        private static float FromPosition(Vector3 position)
        {
            int x = Mathf.RoundToInt(position.x * 10f);
            int z = Mathf.RoundToInt(position.z * 10f);
            // A cheap integer hash. The multipliers are odd and coprime so that points on
            // a grid - which spawn positions very often are - do not land on a pattern.
            unchecked
            {
                int hash = x * 73856093 ^ z * 19349663;
                hash = hash & 0x7FFFFFFF;
                return (hash % 10000) / 10000f;
            }
        }
    }
}
