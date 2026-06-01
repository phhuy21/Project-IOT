using System;

[Serializable]
public class OAuthStatusResp
{
    public bool connected;
    public bool has_refresh_token;
    public string email;
    public string error;
}
