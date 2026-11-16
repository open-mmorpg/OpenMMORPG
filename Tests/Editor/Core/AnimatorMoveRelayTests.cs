using System.Reflection;
using MultiplayerARPG;
using NUnit.Framework;
using UnityEngine;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Unity sends OnAnimatorMove only to the Animator's own GameObject. An entity that keeps its
    /// model on a child never got it on its movement component, so root motion did nothing at all.
    /// The relay sits next to such an Animator and forwards the message; these tests cover where
    /// relays are attached and what they forward. (The motion itself needs a playing Animator.)
    /// </summary>
    [Category(TestCategories.Logic)]
    public class AnimatorMoveRelayTests : KitTestFixture
    {
        private GameObject _entity;
        private RelayTestReceiver _receiver;

        [SetUp]
        public void SetUp()
        {
            _entity = Objects.NewGameObject("Entity");
            _receiver = _entity.AddComponent<RelayTestReceiver>();
        }

        private Animator NewChildAnimator(string name, bool active = true)
        {
            GameObject child = new GameObject(name);
            child.transform.SetParent(_entity.transform, false);
            Animator animator = child.AddComponent<Animator>();
            child.SetActive(active);
            return animator;
        }

        private static void SendAnimatorMove(AnimatorMoveRelay relay)
        {
            // Unity does not send the message in edit mode
            typeof(AnimatorMoveRelay)
                .GetMethod("OnAnimatorMove", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(relay, null);
        }

        [Test]
        public void AnAnimatorOnAChildGetsARelay()
        {
            Animator animator = NewChildAnimator("Model");

            AnimatorMoveRelay relay = AnimatorMoveRelay.Attach(animator, _receiver);

            Assert.That(relay, Is.Not.Null);
            Assert.That(relay.gameObject, Is.SameAs(animator.gameObject));
            Assert.That(relay.Animator, Is.SameAs(animator));
            Assert.That(relay.Receiver, Is.SameAs(_receiver));
        }

        [Test]
        public void AnAnimatorNextToTheReceiverNeedsNoRelay()
        {
            // Unity sends the message to the receiver itself there
            Animator animator = _entity.AddComponent<Animator>();

            AnimatorMoveRelay relay = AnimatorMoveRelay.Attach(animator, _receiver);

            Assert.That(relay, Is.Null);
            Assert.That(_entity.GetComponent<AnimatorMoveRelay>(), Is.Null);
        }

        [Test]
        public void AttachingTwiceKeepsOneRelay()
        {
            Animator animator = NewChildAnimator("Model");

            AnimatorMoveRelay first = AnimatorMoveRelay.Attach(animator, _receiver);
            AnimatorMoveRelay second = AnimatorMoveRelay.Attach(animator, _receiver);

            Assert.That(second, Is.SameAs(first));
            Assert.That(animator.GetComponents<AnimatorMoveRelay>(), Has.Length.EqualTo(1));
        }

        [Test]
        public void EveryModelUnderTheEntityGetsARelayEvenAHiddenOne()
        {
            Animator shown = NewChildAnimator("Model");
            shown.gameObject.AddComponent<RelayTestModel>().animator = shown;
            Animator hidden = NewChildAnimator("ModelRiding");
            hidden.gameObject.AddComponent<RelayTestModel>().animator = hidden;
            hidden.gameObject.SetActive(false);
            Animator notAModel = NewChildAnimator("Prop");

            AnimatorMoveRelay.AttachToModels(_entity, null, _receiver);

            Assert.That(shown.GetComponent<AnimatorMoveRelay>(), Is.Not.Null);
            Assert.That(hidden.GetComponent<AnimatorMoveRelay>(), Is.Not.Null);
            Assert.That(notAModel.GetComponent<AnimatorMoveRelay>(), Is.Null,
                "an Animator that is not a model's keeps handling its own root motion");
        }

        [Test]
        public void TheFallbackAnimatorGetsARelayWhenNoModelNamesOne()
        {
            Animator animator = NewChildAnimator("Model");

            AnimatorMoveRelay.AttachToModels(_entity, animator, _receiver);

            Assert.That(animator.GetComponent<AnimatorMoveRelay>(), Is.Not.Null);
        }

        [Test]
        public void TheRelayForwardsTheMessageWithItsAnimator()
        {
            Animator animator = NewChildAnimator("Model");
            AnimatorMoveRelay relay = AnimatorMoveRelay.Attach(animator, _receiver);

            SendAnimatorMove(relay);

            Assert.That(_receiver.Calls, Is.EqualTo(1));
            Assert.That(_receiver.LastAnimator, Is.SameAs(animator));
        }

        [Test]
        public void ADestroyedReceiverIsNotCalled()
        {
            Animator animator = NewChildAnimator("Model");
            AnimatorMoveRelay relay = AnimatorMoveRelay.Attach(animator, _receiver);
            RelayTestReceiver receiver = _receiver;
            Object.DestroyImmediate(_receiver);

            Assert.DoesNotThrow(() => SendAnimatorMove(relay));
            Assert.That(receiver.Calls, Is.EqualTo(0));
        }
    }

    internal class RelayTestReceiver : MonoBehaviour, IAnimatorMoveReceiver
    {
        public int Calls;
        public Animator LastAnimator;

        public void OnChildAnimatorMove(Animator animator)
        {
            Calls++;
            LastAnimator = animator;
        }
    }

    internal class RelayTestModel : MonoBehaviour, IModelWithAnimator
    {
        public Animator animator;
        public Animator Animator => animator;
    }
}
