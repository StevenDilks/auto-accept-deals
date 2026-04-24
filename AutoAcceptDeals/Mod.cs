using MelonLoader;

[assembly: MelonInfo(typeof(AutoAcceptDeals.Mod), "AutoAcceptDeals", "0.1.0", "Steven Dilks")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace AutoAcceptDeals;

public class Mod : MelonMod
{
    public override void OnInitializeMelon()
    {
        LoggerInstance.Msg("AutoAcceptDeals loaded");

        // TODO: hook the game's incoming-deal handler and auto-accept.
        // Needs class/method names from a fresh dump of Il2CppAssembly-CSharp
        // (look for the customer/contract/deal subsystem).
    }
}
