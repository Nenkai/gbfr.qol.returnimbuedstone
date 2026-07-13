using gbfr.qol.returnimbuedstone.Configuration;
using gbfr.qol.returnimbuedstone.Template;

using gbfrelink.utility.manager.Interfaces;

using NenTools.Reloaded.ScanManager.Interfaces;

using Reloaded.Hooks.Definitions;
using Reloaded.Memory.Sigscan;
using Reloaded.Memory.Sigscan.Definitions.Structs;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;

using System.Diagnostics;
using System.Net;

using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace gbfr.qol.returnimbuedstone;

/// <summary>
/// Your mod logic goes here.
/// </summary>
public unsafe class Mod : ModBase // <= Do not Remove.
{
    /// <summary>
    /// Provides access to the mod loader API.
    /// </summary>
    private readonly IModLoader _modLoader;

    /// <summary>
    /// Provides access to the Reloaded.Hooks API.
    /// </summary>
    /// <remarks>This is null if you remove dependency on Reloaded.SharedLib.Hooks in your mod.</remarks>
    private readonly IReloadedHooks? _hooks;

    /// <summary>
    /// Provides access to the Reloaded logger.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// Entry point into the mod, instance that created this class.
    /// </summary>
    private readonly IMod _owner;

    /// <summary>
    /// Provides access to this mod's configuration.
    /// </summary>
    private Config _configuration;

    /// <summary>
    /// The configuration of the currently executing mod.
    /// </summary>
    private readonly IModConfig _modConfig;

    private static IStartupScanner? _startupScanner = null!;

    private readonly IUserDefinedParams? _userDefinedParams;

    private byte* _itemManagerGlobal; // This will be fetched from hooking the give item function
    private nint _weaponManagerPtr;
    private nint _currentCharacterStatePtr;

    private GivePendulumDelegate Wrapper_AddPendulum;
    public delegate void GivePendulumDelegate(byte* itemGlobal, uint pendulumItemId, bool flag);

    private IHook<OnDialogEventDelegate> _blacksmithDialogHook;
    public delegate void OnDialogEventDelegate(byte* a1); // Should be BlacksmithPendulumDialog

    private IHook<GeneratePendulumDataDelegate> _generatePendulumData;
    public delegate void GeneratePendulumDataDelegate(byte* this_, PendulumData* pPendulumData);

    private IHook<OnGiveItemDelegate> _giveItemHook;
    public delegate void OnGiveItemDelegate(byte* a1, uint itemIdHash, uint count, bool flag);

    public Mod(ModContext context)
    {
        _modLoader = context.ModLoader;
        _hooks = context.Hooks;
        _logger = context.Logger;
        _owner = context.Owner;
        _configuration = context.Configuration;
        _modConfig = context.ModConfig;

#if DEBUG
        Debugger.Launch();
#endif

        var startupScannerController = _modLoader.GetController<IStartupScanner>();
        if (startupScannerController == null || !startupScannerController.TryGetTarget(out _startupScanner))
        {
            _logger.WriteLine($"[{_modConfig.ModId}] IStartupScanner not found?", System.Drawing.Color.Red);
            return;
        }

        var userDefinedParamsRef = _modLoader.GetController<IUserDefinedParams>();
        if (startupScannerController == null || !userDefinedParamsRef.TryGetTarget(out _userDefinedParams))
        {
            _logger.WriteLine($"[{_modConfig.ModId}] IUserDefinedParams not found?", System.Drawing.Color.Red);
            return;
        }

        var scanManagerRef = _modLoader.GetController<IScanManager>();
        if (startupScannerController == null || !scanManagerRef.TryGetTarget(out IScanManager? scanManager))
        {
            _logger.WriteLine($"[{_modConfig.ModId}] IScanManager not found?", System.Drawing.Color.Red);
            return;
        }

        string signaturesFolder = Path.Combine(_modLoader.GetDirectoryForModId(_modConfig.ModId), "Signatures");
        scanManager.InitializeScans(signaturesFolder, _modConfig.ModId);

        string sourceGroup = _userDefinedParams.IsEndlessRagnarok() ? "granblue_fantasy_relink_er" : "granblue_fantasy_relink";

        scanManager.AddScan("GiveStoneItem", sourceGroup, (addr) =>
            Wrapper_AddPendulum = _hooks!.CreateWrapper<GivePendulumDelegate>(addr, out nint wrapperAddress));

        // Hook the dialog result when imbuing
        scanManager.AddScan("ui_Component_ControllerBlackSmithPendulumDialog_OnDialogEvent", sourceGroup, (addr) =>
            _blacksmithDialogHook = _hooks.CreateHook<OnDialogEventDelegate>(ui__Component__ControllerBlackSmithPendulumDialog__OnDialogEvent_Hook, addr).Activate());

        // Hook the function responsible for generating pendulum data
        scanManager.AddScan("GeneratePendulumData", sourceGroup, (addr) =>
            _generatePendulumData = _hooks.CreateHook<GeneratePendulumDataDelegate>(GeneratePendulumData_Hook, addr).Activate());

        scanManager.AddScan("WeaponManager_Ptr", sourceGroup, (addr) =>
            _weaponManagerPtr = addr + (*(int*)(addr + 3)) + 7);

        scanManager.AddScan("CurrentCharacterState_Ptr", sourceGroup, (addr) =>
            _currentCharacterStatePtr = addr + (*(int*)(addr + 3)) + 7);

        scanManager.AddScan("ItemManager_OnGiveItem", sourceGroup, (addr) =>
            _giveItemHook = _hooks!.CreateHook<OnGiveItemDelegate>(ItemManager_OnGiveItem_Hook, addr).Activate());

        /*
        // Hook function that gets a skill for a pendulum
        SigScan("41 57 41 56 56 57 55 53 48 83 EC ?? 44 89 C3 49 89 CF 44 89 C5", "", address =>
        {
            _selectPendulumSkillHook = _hooks.CreateHook<SelectPendulumSkillDelegate>(SelectPendulumSkill, address).Activate();
            _logger.WriteLine($"[gbfr.qol.returnimbuedstone] Successfully hooked SelectPendulumSkill", _logger.ColorGreen);
        });

        // Hook function that gets a level for a skill
        SigScan("41 56 56 57 55 53 48 83 EC ?? 45 89 C6 41 83 F8", "", address =>
        {
            _selectPendulumSkillLevelHook = _hooks.CreateHook<SelectPendulumSkillLevelDelegate>(SelectPendulumSkillLevel, address).Activate();
            _logger.WriteLine($"[gbfr.qol.returnimbuedstone] Successfully hooked SelectPendulumSkillLevel", _logger.ColorGreen);
        });
        */
    }

    private bool _givingPendulumBack = false;
    private WeaponSaveDataUnit _oldWp;

    // ui::component::ControllerBlacksmithPendulumDialog::OnDialogEvent
    private void ui__Component__ControllerBlackSmithPendulumDialog__OnDialogEvent_Hook(byte* this_)
    {
        // 3FCF6BB6|BlacksmithPendulumDialog
        // E261AA72|BlacksmithPendulumResultDialog <-- we want this one - this is the screen when imbuing is actually being performed
        if (*(this_ + 0x240) == 0) // There's two dialogs. 0 = confirm, 1 = actually perform it
        {
            _blacksmithDialogHook.OriginalFunction(this_);
            return;
        }

        // if (*(a1 + 0x240) == 1) // Pressed OK? otherwise 0 is cancel
        // ^ check removed, this is never 0 anyway since the only option is OK

        WeaponSaveDataUnit* pCurrentWeapon = null;
        int saveDataAddr = _userDefinedParams.IsEndlessRagnarok () ? 0x370 : 0x1B0;
        int charaSize = _userDefinedParams.IsEndlessRagnarok() ? 0x680 : 0x500;
        int weaponSize = _userDefinedParams.IsEndlessRagnarok() ? 0xD0 : 0xA0;

        int weaponSlotId = *(int*)((*(nint*)_currentCharacterStatePtr) + 0xD4);

        for (int characterIndex = 0; characterIndex < 32; characterIndex++) // Each character?
        {
            for (int weaponIndex = 0; weaponIndex < 8; weaponIndex++) // Each weapon?
            {
                WeaponSaveDataUnit* pWeapon = (WeaponSaveDataUnit*)((byte*)*(long*)_weaponManagerPtr + saveDataAddr + 
                    (characterIndex * charaSize) + 
                    (weaponIndex * weaponSize));

                if (pWeapon->SaveSlotIDMaybe == weaponSlotId)
                {
                    pCurrentWeapon = pWeapon;
                    break;
                }
            }

            if (pCurrentWeapon is not null)
                break;
        }

        // Copy the struct containing the old pendulum while we can
        _oldWp = new WeaponSaveDataUnit();
        _oldWp = *pCurrentWeapon;

        // Let the original function do its thing, the data unit will be changed with the new pendulum
        _blacksmithDialogHook.OriginalFunction(this_);

        // Original function has likely altered the pendulum now, so add the old one back to the inventory
        if (pCurrentWeapon is not null)
        {
            // Old weapon has a pendulum?
            if (_oldWp.PendulumData.PendulumItemId != XXHash32Custom.Hash(""))
            {
                //_traitIdx = 1;

                _givingPendulumBack = true;
                Wrapper_AddPendulum(_itemManagerGlobal, _oldWp.PendulumData.PendulumItemId, false);
                _givingPendulumBack = false;

                _logger.WriteLine($"[gbfr.qol.returnimbuedstone] Stone {_oldWp.PendulumData.PendulumItemId:X8} ({_oldWp.PendulumData.Skill1:X8}/{_oldWp.PendulumData.Skill2:X8}/{_oldWp.PendulumData.Skill3:X8}, " +
                    $"Lv{_oldWp.PendulumData.Skill1Level}/{_oldWp.PendulumData.Skill2Level}/{_oldWp.PendulumData.Skill3Level}) " +
                    $"for Weapon {_oldWp.WeaponId:X8} (+{_oldWp.MirageCount}, {_oldWp.UncapTier}* Uncap) added back to inventory.");
            }
        }
    }

    public void GeneratePendulumData_Hook(byte* a1, PendulumData* pPendulumData)
    {
        // This function solely just generates pendulum data into the second argument
        if (_givingPendulumBack)
            *pPendulumData = _oldWp.PendulumData; // We are giving back the stone, instead of generating, just set the old stats right away
        else
            _generatePendulumData.OriginalFunction(a1, pPendulumData);
    }

    private void ItemManager_OnGiveItem_Hook(byte* @this, uint itemId, uint count, bool flag)
    {
        // Grab item manager pointer
        if (_itemManagerGlobal == null)
            _itemManagerGlobal = @this;

        _giveItemHook.OriginalFunction(@this, itemId, count, flag);
        _giveItemHook.Disable();
    }

    #region Standard Overrides
    public override void ConfigurationUpdated(Config configuration)
    {
        // Apply settings from configuration.
        // ... your code here.
        _configuration = configuration;
        _logger.WriteLine($"[{_modConfig.ModId}] Config Updated: Applying");
    }
    #endregion

    #region For Exports, Serialization etc.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public Mod() { }
#pragma warning restore CS8618
    #endregion


    /* OLD METHOD (didn't work for main stone trait)
    
    private int _traitIdx = 1;

    private IHook<SelectPendulumSkillDelegate> _selectPendulumSkillHook;
    public delegate int* SelectPendulumSkillDelegate(byte* a1, uint* retSkill, int* a3);

    private IHook<SelectPendulumSkillLevelDelegate> _selectPendulumSkillLevelHook;
    public delegate uint SelectPendulumSkillLevelDelegate(byte* a1, uint a2, uint a3);

    /// <summary>
    /// Fired when a skill for a pendulum needs to be selected
    /// </summary>
    /// <param name="a1"></param>
    /// <param name="retSkill"></param>
    /// <param name="a3"></param>
    /// <returns></returns>
    private int* SelectPendulumSkill(byte* a1, uint* retSkill, int* a3)
    {
        if (_givingPendulumBack)
        {
            if (_traitIdx == 1)
                *retSkill = _oldWeaponState.Skill2;
            else if (_traitIdx == 2)
                *retSkill = _oldWeaponState.Skill3;

            // Do not increment _traitIdx, skill level is called right after so let it do it instead
            return a3;
        }
        else
            return _selectPendulumSkillHook.OriginalFunction(a1, retSkill, a3);
    }

    /// <summary>
    /// Fired when a level for a pendulum skill needs to be selected
    /// </summary>
    /// <param name="a1"></param>
    /// <param name="a2"></param>
    /// <param name="a3"></param>
    /// <returns></returns>
    private uint SelectPendulumSkillLevel(byte* a1, uint a2, uint a3)
    {
        if (_givingPendulumBack)
        {
            uint level = a3;
            if (_traitIdx == 1)
                level = _oldWeaponState.Skill2Level;
            else if (_traitIdx == 2)
                level = _oldWeaponState.Skill3Level;

            _traitIdx++;
            return level;
        }
        else
            return _selectPendulumSkillLevelHook.OriginalFunction(a1, a2, a3);
    }
    */
}