using System.Globalization;
using HotSonos.Remote.Protocol;

namespace HotSonos.Remote.Views;

/// <summary>Asks for the pairing password after the saved PIN is rejected. The password is not saved.</summary>
sealed class PasswordPairPage : ContentPage
{
    readonly Entry _host = RemoteUi.Field("PC address or 192.168.1.10");
    readonly Entry _port = RemoteUi.Field("Port (blank = 42342)", Keyboard.Numeric);
    readonly Entry _password = RemoteUi.Field("Pairing password");
    readonly Label _status = RemoteUi.Muted(PairingPrompt.AskAgain);
    bool _paired;

    public PasswordPairPage()
    {
        Title = "Pair with password";
        BackgroundColor = Swatches.Bg;
        NavigationPage.SetHasNavigationBar(this, false);
        _password.IsPassword = true;
        _password.MaxLength = PairingPassword.MaxLength;
        var saved = AppServices.Connection;
        _host.Text = saved.Host;
        _port.Text = saved.Port.ToString(CultureInfo.InvariantCulture);
        Content = RemoteUi.ConnectPage(
            "Pair with password",
            _status,
            RemoteUi.Muted("The PC rejected the saved PIN. The password is not saved on this phone."),
            _host,
            _port,
            _password,
            RemoteUi.Button("Pair with password", PairAsync),
            RemoteUi.Button("Cancel", CancelAsync));
    }

    async Task PairAsync()
    {
        var secret = _password.Text ?? "";
        var (ok, message) = await PairingHandoff.PairWithPasswordAsync(_host.Text, _port.Text, secret).ConfigureAwait(true);
        if (ok)
        {
            _password.Text = "";
            _paired = true;
            _status.Text = message;
            await Navigation.PopModalAsync().ConfigureAwait(true);
            return;
        }

        _status.Text = message;
    }

    async Task CancelAsync()
    {
        _password.Text = "";
        await Navigation.PopModalAsync().ConfigureAwait(true);
    }

    protected override void OnDisappearing()
    {
        if (_paired)
            PairingPrompt.Closed();
        else
            PairingPrompt.Dismissed();
        base.OnDisappearing();
    }
}
