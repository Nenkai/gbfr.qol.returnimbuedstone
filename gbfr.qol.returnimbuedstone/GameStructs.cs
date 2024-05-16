using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace gbfr.qol.returnimbuedstone;

public struct WeaponSaveDataUnit
{
    public int SaveSlotIDMaybe;
    public int WeaponId;
    public int field_8;
    public int field_C;
    public int XP;
    public int UncapTier;
    public int MirageCount;
    public int field_1C;
    public PendulumData PendulumData;

    public override readonly string ToString()
    {
        return $"- Slot ID: {SaveSlotIDMaybe}\n" +
               $"- Weapon ID: {WeaponId:X8}\n" +
               $"- Trait 1: {PendulumData.Skill1:X8} (Lv{PendulumData.Skill1Level})\n" +
               $"- Trait 2: {PendulumData.Skill2:X8} (Lv{PendulumData.Skill2Level})\n" +
               $"- Trait 3: {PendulumData.Skill3:X8} (Lv{PendulumData.Skill3Level})\n" +
               $"- Mirage: {MirageCount}\n" +
               $"- Uncap Tier: {UncapTier}\n" +
               $"- Stone ID: {PendulumData.PendulumItemId:X8}";
    }
};

public struct PendulumData
{
    public uint Skill1;
    public uint Skill1Level;
    public uint Skill2;
    public uint Skill2Level;
    public uint Skill3;
    public uint Skill3Level;
    public uint PendulumItemId;
}
