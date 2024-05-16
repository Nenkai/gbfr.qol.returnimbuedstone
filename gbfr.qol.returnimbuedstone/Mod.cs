using gbfr.qol.returnimbuedstone.Configuration;
using gbfr.qol.returnimbuedstone.Template;

using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;

using System.Diagnostics;

using Reloaded.Hooks.Definitions;
using Reloaded.Memory.Interfaces;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;
using System;
using Reloaded.Hooks.Definitions.Structs;
using System.Runtime.CompilerServices;

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

    private nint _imageBase;

    private GivePendulumDelegate Wrapper_AddPendulum;
    public unsafe delegate void GivePendulumDelegate(byte* itemGlobal, uint pendulumItemId, bool flag);

    private IHook<OnDialogEventDelegate> _blacksmithDialogHook;
    public delegate void OnDialogEventDelegate(byte* a1, uint dialogId); // Should be BlacksmithPendulumDialog

    private IHook<GeneratePendulumDataDelegate> _generatePendulumData;
    public delegate void GeneratePendulumDataDelegate(byte* a1, PendulumData* pPendulumData);

    private IHook<SelectPendulumSkillDelegate> _selectPendulumSkillHook;
    public delegate int* SelectPendulumSkillDelegate(byte* a1, uint* retSkill, int* a3);

    private IHook<SelectPendulumSkillLevelDelegate> _selectPendulumSkillLevelHook;
    public delegate uint SelectPendulumSkillLevelDelegate(byte* a1, uint a2, uint a3);

    public Mod(ModContext context)
    {
        _modLoader = context.ModLoader;
        _hooks = context.Hooks;
        _logger = context.Logger;
        _owner = context.Owner;
        _configuration = context.Configuration;
        _modConfig = context.ModConfig;

        var startupScannerController = _modLoader.GetController<IStartupScanner>();
        if (startupScannerController == null || !startupScannerController.TryGetTarget(out _startupScanner))
        {
            return;
        }

#if DEBUG
        Debugger.Launch();
#endif

        _imageBase = Process.GetCurrentProcess().MainModule!.BaseAddress;
        var memory = Reloaded.Memory.Memory.Instance;

        // Get function that gives pendulums
        SigScan("55 41 57 41 56 41 55 41 54 56 57 53 48 81 EC ?? ?? ?? ?? 48 8D AC 24 ?? ?? ?? ?? 48 C7 45 ?? ?? ?? ?? ?? 45 89 C6 89 D7 48 89 CE", "", address =>
        {
            Wrapper_AddPendulum = _hooks!.CreateWrapper<GivePendulumDelegate>(address, out nint wrapperAddress);
            _logger.WriteLine($"[gbfr.qol.returnimbuedstone] Registered GiveStoneItem (0x{address:X8})", _logger.ColorGreen);
        });

        // Hook the dialog result when imbuing
        SigScan("55 41 57 41 56 41 55 41 54 56 57 53 48 81 EC ?? ?? ?? ?? 48 8D AC 24 ?? ?? ?? ?? 48 83 E4 ?? 48 89 E3 48 89 AB ?? ?? ?? ?? 48 C7 85 ?? ?? ?? ?? ?? ?? ?? ?? 48 89 CE 48 8B 05", "", address =>
        {
            _blacksmithDialogHook = _hooks.CreateHook<OnDialogEventDelegate>(ui__Component__ControllerBlackSmithPendulumDialog__OnDialogEvent, address).Activate();
            _logger.WriteLine($"[gbfr.qol.returnimbuedstone] Successfully hooked ui::Component::ControllerBlackSmithPendulumDialog::OnDialogEvent", _logger.ColorGreen);
        });

        // Hook the function responsible for generating pendulum data
        SigScan("55 41 57 41 56 41 55 41 54 56 57 53 48 83 EC ?? 48 8D 6C 24 ?? 48 C7 45 ?? ?? ?? ?? ?? 49 89 D6 8B 52", "", address =>
        {
            _generatePendulumData = _hooks.CreateHook<GeneratePendulumDataDelegate>(GeneratePendulumData, address).Activate();
            _logger.WriteLine($"[gbfr.qol.returnimbuedstone] Successfully hooked GeneratePendulumData", _logger.ColorGreen);
        });

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

    private void SigScan(string pattern, string name, Action<nint> action)
    {
        _startupScanner?.AddMainModuleScan(pattern, result =>
        {
            if (!result.Found)
            {
                return;
            }
            action(_imageBase + result.Offset);
        });
    }

    private bool _givingPendulumBack = false;
    private WeaponSaveDataUnit _oldWp;
    private int _traitIdx = 1;

    // ui::component::ControllerBlacksmithPendulumDialog::OnDialogEvent
    private void ui__Component__ControllerBlackSmithPendulumDialog__OnDialogEvent(byte* a1, uint dialogId)
    {
        // 3FCF6BB6|BlacksmithPendulumDialog
        // E261AA72|BlacksmithPendulumResultDialog <-- we want this one - this is the screen when imbuing is actually being performed
        if (dialogId != 0xE261AA72)
        {
            _blacksmithDialogHook.OriginalFunction(a1, dialogId);
            return;
        }


        WeaponSaveDataUnit* target = null;
        if (*(a1 + 0x240) == 1) // Pressed OK? otherwise 0 is cancel
        {
            byte* unkGlobal1 = (byte*)*(long*)(_imageBase + 0x68BF3A0);
            byte* saveDataGlobal = (byte*)*(long*)(_imageBase + 0x68BF3F0);
            int weaponSlotId = *(int*)(unkGlobal1 + 0xD4);

            for (int i = 0; i < 32; i++) // Each character?
            {
                for (int j = 0; j < 8; j++) // Each weapon?
                {
                    WeaponSaveDataUnit* ptr = (WeaponSaveDataUnit*)(saveDataGlobal + (i * 0x500) + (0x1B0 + (j * 0xA0)));
                    if (ptr->SaveSlotIDMaybe == weaponSlotId)
                    {
                        target = ptr;
                        break;
                    }
                }

                if (target is not null)
                    break;
            }

            //_logger.WriteLine($"Weapon Slot Address: {(long)target:X8}");
            //_logger.WriteLine("=== OLD ===");
            //_logger.WriteLine((*target).ToString());

            // Copy the struct containing the old pendulum while we can
            _oldWp = new WeaponSaveDataUnit();
            _oldWp = *target;
        }

        // Let the original function do its thing, the data unit will be changed with the new pendulum
        _blacksmithDialogHook.OriginalFunction(a1, dialogId);

        // Original function has likely altered the pendulum now, so add the old one back to the inventory
        if (target is not null)
        {
            //_logger.WriteLine("=== NEW ===");
            //_logger.WriteLine((*target).ToString());

            // Old weapon has a pendulum?
            if (_oldWp.PendulumData.PendulumItemId != XXHash32Custom.Hash(string.Empty))
            {
                byte* itemGlobal = (byte*)*(long*)(_imageBase + 0x68bf3c0);

                _traitIdx = 1;

                _givingPendulumBack = true;
                Wrapper_AddPendulum(itemGlobal, _oldWp.PendulumData.PendulumItemId, false);
                _givingPendulumBack = false;

                _logger.WriteLine($"[gbfr.qol.returnimbuedstone] Stone {_oldWp.PendulumData.PendulumItemId:X8} ({_oldWp.PendulumData.Skill1:X8}/{_oldWp.PendulumData.Skill2:X8}/{_oldWp.PendulumData.Skill3:X8}, " +
                    $"Lv{_oldWp.PendulumData.Skill1Level}/{_oldWp.PendulumData.Skill2Level}/{_oldWp.PendulumData.Skill3Level}) " +
                    $"for Weapon {_oldWp.WeaponId:X8} (+{_oldWp.MirageCount}, {_oldWp.UncapTier}* Uncap) added back to inventory.");
            }
        }
    }

    public void GeneratePendulumData(byte* a1, PendulumData* pPendulumData)
    {
        // This function solely just generates pendulum data into the second argument
        if (_givingPendulumBack)
            *pPendulumData = _oldWp.PendulumData; // We are giving back the stone, instead of generating, just set the old stats right away
        else
            _generatePendulumData.OriginalFunction(a1, pPendulumData);
    }

    /*
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
}