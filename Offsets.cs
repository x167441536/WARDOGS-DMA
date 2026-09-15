// WardogsClient build offsets. Keep updateable memory-layout values here.
internal static class Offsets
{
    internal const ulong GNames = 0x0CE6CA30;
    internal const ulong GWorld = 0x0D0CB998;
    internal const ulong GObjects = 0x0CF3F260;
    internal const ulong NumElements = 0xCF3F274;
    internal const ulong GEngine = 0x0D0C8AD0;
    internal const int PlayerBaseClassIndex = 6060;
    internal const int VehicleBaseClassIndex = 3650;
    internal const int ModularVehicleClassIndex = 3346;

    internal const ulong ActorClass = 0x10;
    internal const ulong ActorRootComponent = 0x1C0;
    // USceneComponent::RelativeLocation (FVector, three 32-bit floats).
    internal const ulong SceneRelativeLocation = 0x168;
    // USceneComponent::RelativeRotation (FRotator: Pitch/Yaw/Roll, 0x18 in
    // the supplied LWC SDK). RelativeLocation occupies 24 bytes in this
    // build, so the next reflected member starts at +0x180.
    internal const ulong SceneRelativeRotation = 0x180;
    internal const ulong MoverMesh = 0x390;
    internal const ulong VehicleMesh = 0x750;
    // AWDMoverCharacter's UWDWeaponBehaviorComponent (SDK 10.09.2026).
    // The component contains the runtime ADS state/multiplier used by
    // GetAdsFovMultiplier(); reading the instance avoids calling game code.
    // The SDK's generated member labels are shifted by obfuscation: the
    // UWDWeaponBehaviorComponent type is at 0x770, while the 0x778 member
    // named WeaponBehaviorComponent is actually UWDVoxSystemComponent.
    internal const ulong WeaponBehaviorComponent = 0x770;
    // UWDWeaponBehaviorComponent runtime fields observed in the SDK layout.
    // 0x49C/0x4A0 are the two float slots immediately following the weapon
    // stats pointer and are used as the current/base ADS FOV multipliers.
    internal const ulong WeaponAdsMultiplier = 0x49C;
    internal const ulong WeaponAdsBaseMultiplier = 0x4A0;
    // FWDAimSwayState::bFocusing; useful as an additional ADS activity hint.
    internal const ulong WeaponAimingHint = 0x25C;
    // AWDMoverCharacter::AimingAlphaByte, replicated ADS activity flag.
    internal const ulong CharacterAimingAlpha = 0x81C;
    // AWDMoverCharacter::PlayerInventory (SDK 10.09.2026).
    internal const ulong PlayerInventory = 0x748;
    // UWDPlayerInventoryComponent::_aaaaich / held item (SDK 10.09.2026).
    internal const ulong InventoryHeldItem = 0x8C8;
    // UWDPlayerInventoryComponent::_aaabeepimlfomimgndf, active-item fallback.
    internal const ulong InventoryActiveItem = 0xA78;
    // USceneComponent::ComponentToWorld is not emitted by the generated SDK
    // in this build.  UE5/LWC alignment places the FTransform immediately
    // after the replicated flags block; keep a short ordered probe list so a
    // minor hotfix does not silently move ESP boxes to stale coordinates.
    internal const ulong MeshComponentToWorld = 0x1D0;
    internal static readonly ulong[] ComponentToWorldCandidates =
    {
        0x1D0, 0x1E0, 0x210
    };
    // USkeletalMeshComponent -> USkinnedAsset. The asset owns the reference
    // skeleton used to name and parent the currently equipped mesh.
    internal const ulong SkinnedAsset = 0x5B0;
    // USkinnedAsset -> FReferenceSkeleton::RawRefBoneInfo TArray.
    internal const ulong RawRefBoneInfo = 0x2E0;
    internal const int MeshBoneInfoSize = 0x0C;
    internal const ulong PawnPlayerState = 0x2D8;
    internal const ulong ControllerPawn = 0x2F8;
    internal const ulong PlayerStatePawn = 0x330;
    // APlayerController::AcknowledgedPawn in the current Wardogs build.
    // Keep the older 0x2F8/0x308 probes as fallbacks for transitional builds.
    internal const ulong ControllerPawnCurrent = 0x360;
    internal const ulong PlayerStateFactionComponent = 0x4F0;
    internal const ulong VitalityComponent = 0x738;
    internal const ulong BleedoutState = 0x810;
    internal const ulong VehiclePlayerState = 0x848;
    internal const ulong StationaryVehicleFactionComponent = 0x0AC8;
    internal const ulong AirplaneVehicleFactionComponent = 0x0BA8;
    internal const ulong RotaryVehicleFactionComponent = 0x0B88;
    internal const ulong TrackedVehicleFactionComponent = 0x0BE8;
    internal const ulong WheeledVehicleFactionComponent = 0x0BD8;
    // Vehicle subclasses keep their own UWDFactionComponent at different
    // offsets (air/rotary/tracked/wheeled). Probe all SDK-confirmed slots
    // before falling back to the occupying PlayerState.
    internal static readonly ulong[] VehicleFactionComponentCandidates =
    {
        0x0BA8, 0x0B88, 0x0BE8, 0x0BD8, 0x0AC8,
        0x0320, 0x0308, 0x02C8, 0x0370, 0x03E0
    };
    // APlayerState::SavedNetworkAddress is 0x308; the SDK's authoritative
    // player-name FString is PlayerNamePrivate at 0x350. No separate
    // SanitizedName storage is emitted in this SDK, so both reads use the
    // canonical name slot rather than interpreting the network address as a
    // name.
    internal const ulong SavedNetworkAddress = 0x308;
    internal const ulong PlayerName = 0x350;
    internal const ulong SanitizedName = 0x350;

    internal const ulong FactionComponentFaction = 0x128;
    // UWDFactionComponent::GetFactionTag() returns the replicated gameplay
    // tag at this slot. 0x100 is retained as a compatibility fallback for
    // transitional layouts where the pending tag is stored first.
    internal const ulong FactionComponentTag = 0x110;
    internal const ulong FactionComponentTagAlt = 0x100;
    internal const ulong FactionData = 0x2C8;
    // UWDFactionData::FactionTag (FGameplayTag/FName) labels the
    // authoritative LONESTAR/VALKYRA/MANTICORE faction.
    internal const ulong FactionTag = 0x30;
    internal const ulong GenericTeamId = 0x108;
    internal const ulong VitalityCurrent = 0x130;
    internal const ulong VitalityMax = 0x134;

    internal const ulong CameraManager = 0x370;
    // AController::ControlRotation in the supplied SDK (FRotator, 3 doubles
    // in the current LWC build).
    internal const ulong ControllerControlRotation = 0x330;
    internal const ulong CurrentCameraCache = 0x1560;
    // FCameraCacheEntry::POV -> LWC FMinimalViewInfo; Location 0x00,
    // Rotation 0x18, FOV 0x30.
    internal const ulong CameraPov = 0x10;
    // APlayerCameraManager view sources: ViewTarget, PendingViewTarget,
    // CurrentCameraCachePrivate and LastFrameCameraCachePrivate.
    internal static readonly ulong[] CameraCacheCandidates = { 0x350, 0xC40, 0x1560, 0x1E40 };
    internal const ulong GameInstance = 0x0228;
    internal const ulong GameInstanceAlt = 0x12C8;
    internal const ulong WorldPersistentLevel = 0x30;
    internal const ulong WorldLevels = 0x1C8;
    internal const ulong WorldLevelsAlt1 = 0x120;
    internal const ulong WorldLevelsAlt2 = 0x130;
    internal const ulong WorldGameState = 0x1B0;
    internal const ulong GameStatePlayerArray = 0x2D0;
    internal const ulong GameStatePlayerArrayCount = 0x2D8;
    internal const ulong LevelActorsContainer = 0xE0;
    internal const ulong LevelActorsArray = 0x28;
    internal const ulong LevelActorsCount = 0x30;
    internal const ulong UObjectClass = 0x10;
    internal const ulong UObjectNameId = 0x18;
    internal const ulong ObjectItemSize = 0x18;
    // This game's chunked object array has a 16-byte item header; the UObject*
    // slot is at +0x10. The +0x08 slot is flags (0x40000000 in the live dump).
    internal const ulong ObjectItemObject = 0x10;
    internal const int ObjectsPerChunk = 0x10000;
    internal const ulong ClassNameId = 0x18;
    internal const ulong ClassSuper = 0x40;
    internal const ulong ObjectArrayNumChunks = 0x1C;
    internal const ulong UObjectFlags = 0x8;
    internal const ulong ControllerPawnAlt = 0x308;
    internal const ulong LocalPlayers = 0x38;
    internal const ulong LocalPlayerController = 0x30;
    // This client stores FNamePool::Blocks inline at the global address;
    // the first block pointer is at +0x00 (the +0x10 layout is used by
    // standard UE5 builds but is not used by Wardogs).
    internal const ulong NamePoolBlocks = 0x00;
    // Wardogs uses the standard variable-length UE5 FNameEntry layout:
    // the low 16 bits of an id are a byte offset within the name block,
    // with a 2-byte header followed immediately by character data.
    internal const ulong NameEntryHeader = 0x0;
    internal const ulong NameEntryData = 0x2;
    // aliases used by the scanner
    internal const ulong LevelActorContainer = LevelActorsContainer;
    internal const ulong ActorArray = LevelActorsArray;
    internal const ulong ActorArrayCount = LevelActorsCount;

    // USkeletalMeshComponent transform arrays from the Wardogs SDK:
    // BoneSpaceTransforms (0xA08) and CachedComponentSpaceTransforms (0xA18).
    // Prefer the evaluated component-space array so animated bones follow the
    // player rather than the reference pose.
    // UE5 builds place the private pose TArrays in different slots. The SDK
    // omits these private members, so probe the common SkinnedMeshComponent
    // layouts first, then the later component slots used by some builds.
    internal static readonly ulong[] PoseArrayCandidates =
    {
        // Score all layouts: unrelated component fields can look like a
        // plausible pointer, so accepting the first hit produces a static or
        // empty rig. The canonical SDK arrays are tried before compact ones.
        0xA18, 0xA08, 0x630, 0x620,
        0x5B0, 0x5C0, 0x5D0, 0x680, 0x690, 0x6A0, 0x6B0, 0x6C0,
        0x6D0, 0x6E0, 0x6F0, 0x700, 0x710, 0x720, 0x730
    };
    internal const ulong BoneArray = 0x620;
    internal const ulong BoneArrayCache = 0x630;
    internal const int DirectBoneArrayFallbackCount = 256;
    // This build uses LWC-enabled UE5 FTransform: FQuat is four doubles at
    // 0x00 (0x20 bytes), FVector translation is three doubles at 0x20 and
    // scale is three doubles at 0x40; the complete transform is 0x60 bytes.
    internal const ulong FTransformTranslation = 0x20;
    internal const ulong FTransformSize = 0x60;

    // Parent indices for the runtime refactored character skeleton.  The
    // first 177 entries are shared by SK_TP_Male_000 and the 356-bone
    // SKEL_TP_Character_Refactor.  BoneSpaceTransforms stores local-space
    // transforms, so these indices are required to reconstruct component
    // space positions before projecting them.
    // Complete parent map for SKEL_TP_Character_Refactor (356 bones),
    // including backpack/helmet/ghillie attachment chains.  Keeping the
    // complete hierarchy is important when equipment inserts animated
    // corrective and attachment transforms into the evaluated pose buffer.
    internal static readonly int[] RefactoredBoneParents =
    {
        -1,0,1,2,3,4,5,6,7,8,6,10,11,12,12,12,12,12,17,17,17,17,12,22,
        22,22,25,26,27,26,22,30,31,32,31,22,35,36,35,22,39,40,41,40,39,22,45,46,
        47,46,22,11,51,11,53,53,53,11,57,57,57,57,10,10,6,64,65,66,66,66,66,66,
        71,71,71,71,66,76,76,76,79,80,81,80,79,76,85,86,87,86,76,90,91,92,91,76,
        95,96,97,96,76,100,101,100,76,65,105,65,107,107,107,65,111,111,111,111,64,64,6,6,
        6,6,1,122,123,124,124,124,123,128,123,123,131,131,122,134,122,136,122,138,138,138,138,138,
        138,1,145,146,147,147,147,146,151,146,146,154,154,145,157,145,159,145,161,161,161,161,161,161,
        0,168,168,0,171,172,172,0,0,0,6,178,179,179,181,179,183,178,185,178,187,178,189,178,
        191,5,193,194,194,196,196,198,194,200,194,202,194,204,205,194,207,194,209,194,211,194,213,194,
        215,215,217,194,219,193,221,221,223,221,225,193,227,193,229,6,231,232,232,234,235,232,237,238,
        122,240,241,145,243,244,10,246,10,248,64,250,64,252,6,254,6,256,5,258,5,260,5,262,
        4,264,4,266,267,4,269,270,4,272,273,4,275,276,123,278,122,280,146,282,145,284,1,286,
        287,288,1,290,291,292,1,294,295,296,1,298,9,300,301,302,9,304,305,306,307,9,309,310,
        311,312,9,314,315,316,9,318,319,320,321,9,323,324,325,326,9,328,329,330,331,9,333,334,
        335,336,9,338,339,340,9,342,343,344,9,346,347,348,349,9,351,352,353,354
    };

    // Core ESP skeleton edges. Indices are taken from the FModel-extracted
    // ReferenceSkeleton of SK_TP_Male_000 (177 bones) and SK_TPCharacter (162).
    internal static readonly (int A, int B)[] RefactoredBoneEdges =
    {
        (1, 4), (4, 7), (7, 9),
        (7, 10), (10, 11), (11, 12), (12, 22),
        (7, 64), (64, 65), (65, 66), (66, 76),
        (1, 122), (122, 123), (123, 124),
        (1, 145), (145, 146), (146, 147)
    };

    internal static readonly (int A, int B)[] LegacyBoneEdges =
    {
        (1, 4), (4, 7), (7, 9),
        (7, 10), (10, 11), (11, 12), (12, 20),
        (7, 56), (56, 57), (57, 58), (58, 66),
        (1, 106), (106, 107), (107, 108),
        (1, 129), (129, 130), (130, 131)
    };
}
