namespace Bds.Core.Auth;

public static class AuthConstants
{
    // Minecraft for Android. Xbox and Minecraft services only accept Minecraft's own client ids.
    public const string ClientId = "0000000048183522";
    public const string Scope = "service::user.auth.xboxlive.com::MBI_SSL";
    public const string PlayFabTitleId = "20CA2";

    public const string XboxLiveRelyingParty = "http://xboxlive.com";
    public const string MinecraftRelyingParty = "https://multiplayer.minecraft.net/";
    public const string PlayFabRelyingParty = "http://playfab.xboxlive.com/";
}
