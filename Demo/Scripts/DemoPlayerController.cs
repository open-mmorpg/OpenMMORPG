using Insthync.CameraAndInput;
using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// The kit's default controller with the mouse conventions most MMO players already
    /// know.
    ///
    /// The kit's controller already has most of the scheme: WASD moves relative to the
    /// camera and turns the character where it walks, Tab picks the nearest enemy, a
    /// chosen target is attacked and followed until it dies, the wheel zooms, and there
    /// is no crosshair. What it lacks is the mouse. In the kit only the right button
    /// orbits the camera and a click on the ground walks the character there. Here:
    ///
    /// - either button dragged orbits the camera, and the cursor is hidden while it does;
    /// - the right button dragged also turns the character to face where the camera looks;
    /// - a right click on an enemy targets it and starts attacking; a left click on
    ///   anything only targets it (the kit's second click attacks, which is left alone);
    /// - a click on the ground does nothing - no click-to-move.
    ///
    /// Everything else is the kit's, so this is a thin layer over
    /// <see cref="PlayerCharacterController"/>: it reads the mouse after the base has,
    /// and only adds or undoes. Built by DemoControllerBuilder.
    /// </summary>
    public class DemoPlayerController : PlayerCharacterController
    {
        [Header("Mouse")]
        [Tooltip("How far the pointer travels, in pixels, before a held button is a drag rather than a click.")]
        public float dragThreshold = 6f;
        [Tooltip("Hide and lock the cursor while a button is dragging the camera.")]
        public bool lockCursorWhileDragging = true;

        private struct Button
        {
            public bool Down;
            public bool Drag;
            public bool OverUi;
            public Vector3 DownAt;
        }

        private Button _left;
        private Button _right;

        public override void UpdateInput()
        {
            base.UpdateInput();
            if (PlayingCharacterEntity == null)
                return;

            bool focus = GenericUtils.IsFocusInputField();
            bool overUi = UISceneGameplay.IsPointerOverUIObject();
            Track(ref _left, 0, overUi);
            Track(ref _right, 1, overUi);

            bool leftOrbit = _left.Down && _left.Drag && !_left.OverUi;
            bool rightOrbit = _right.Down && !_right.OverUi;
            if (!focus && (leftOrbit || rightOrbit))
                CacheGameplayCameraController.UpdateRotation = true;

            // The right button steers: the character turns with the camera, unless it is
            // mid-swing and the kit is aiming it.
            var cameraController = CacheGameplayCameraController as DefaultGameplayCameraController;
            if (rightOrbit && _right.Drag && !focus && cameraController != null &&
                !PlayingCharacterEntity.IsPlayingAttackOrUseSkillAnimation())
            {
                PlayingCharacterEntity.SetLookRotation(Quaternion.Euler(0f, cameraController.CameraControls.yRotation, 0f), false);
            }

            if (lockCursorWhileDragging)
            {
                bool dragging = leftOrbit || (rightOrbit && _right.Drag);
                Cursor.lockState = dragging ? CursorLockMode.Locked : CursorLockMode.None;
                Cursor.visible = !dragging;
            }

            if (focus)
                return;

            // A right click, not a drag, on an enemy: target it and attack.
            if (InputManager.GetMouseButtonUp(1) && !_right.Drag && !_right.OverUi)
            {
                BaseCharacterEntity enemy = EnemyUnderPointer();
                if (enemy != null)
                    AttackTarget(enemy);
            }

            // A left click on nothing: the base has just told the character to walk
            // there. Take that back; the ground is not a destination here.
            if (InputManager.GetMouseButtonUp(0) && !_left.Drag && !_left.OverUi && EntityUnderPointer() == null)
            {
                _destination = null;
                PlayingCharacterEntity.StopMove();
            }
        }

        /// <summary>
        /// Target an enemy and start attacking it: the kit's second-click behaviour, but
        /// in one call. SetTarget selects on the first call and locks on the second, so it
        /// is called twice on purpose; the kit's click handler then also raises the follow
        /// flag, which is what makes UpdateFollowTarget close in and swing - without it the
        /// target is merely held.
        /// </summary>
        public void AttackTarget(BaseCharacterEntity enemy)
        {
            if (enemy == null || enemy.IsDead())
                return;
            SetTarget(enemy, TargetActionType.Attack);
            SetTarget(enemy, TargetActionType.Attack);
            _isFollowingTarget = true;
        }

        private void Track(ref Button button, int index, bool pointerOverUi)
        {
            if (InputManager.GetMouseButtonDown(index))
            {
                button.Down = true;
                button.Drag = false;
                button.OverUi = pointerOverUi;
                button.DownAt = InputManager.MousePosition();
            }
            if (button.Down && !button.Drag &&
                (InputManager.MousePosition() - button.DownAt).sqrMagnitude > dragThreshold * dragThreshold)
                button.Drag = true;
            // Kept through the frame the button comes up, so the click handlers can still
            // ask whether it was a drag; cleared the frame after.
            if (!InputManager.GetMouseButton(index) && !InputManager.GetMouseButtonUp(index))
            {
                button.Down = false;
                button.Drag = false;
            }
        }

        private BaseGameEntity EntityUnderPointer()
        {
            int count = FindClickObjects(out _);
            for (int i = 0; i < count; ++i)
            {
                Transform hit = _physicFunctions.GetRaycastTransform(i);
                if (hit == null)
                    continue;
                BaseGameEntity entity = hit.GetComponentInParent<BaseGameEntity>();
                if (entity != null && entity != PlayingCharacterEntity)
                    return entity;
            }
            return null;
        }

        private BaseCharacterEntity EnemyUnderPointer()
        {
            var character = EntityUnderPointer() as BaseCharacterEntity;
            if (character == null || character.IsDead())
                return null;
            return PlayingCharacterEntity.IsEnemy(character.GetInfo()) ? character : null;
        }
    }
}
