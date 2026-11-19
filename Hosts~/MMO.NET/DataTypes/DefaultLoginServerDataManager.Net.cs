namespace MultiplayerARPG.MMO
{
    // The .NET half of the kit's DefaultLoginServerDataManager (Server/Login). The kit's half keeps
    // character ids and access tokens; these two read the character creation rules from
    // Config/characterCreationData.json instead of Unity's GameInstance.
    public partial class DefaultLoginServerDataManager
    {
        public bool CanCreateCharacter(ref int dataId, ref int entityId, ref int factionId, IList<CharacterDataBoolean> publicBools, IList<CharacterDataInt32> publicInts, IList<CharacterDataFloat32> publicFloats, out UITextKeys errorMessage)
        {
            errorMessage = UITextKeys.NONE;
            return DataManager.CharacterCreationData.CanCreateCharacter(dataId, entityId, factionId, publicBools, publicInts, publicFloats);
        }

        public void SetNewPlayerCharacterData(PlayerCharacterData playerCharacterData, string characterName, int dataId, int entityId, int factionId, IList<CharacterDataBoolean> publicBools, IList<CharacterDataInt32> publicInts, IList<CharacterDataFloat32> publicFloats)
        {
            DataManager.CharacterCreationData.SetCreateCharacterData(playerCharacterData, playerCharacterData.Id, playerCharacterData.UserId, characterName, dataId, entityId, factionId, publicBools, publicInts, publicFloats);
        }
    }
}
