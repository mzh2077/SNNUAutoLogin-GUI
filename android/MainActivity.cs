using System;
using System.Threading;
using System.Threading.Tasks;
using Android;
using Android.App;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using SNNUAutoLogin.Services;

[assembly: UsesPermission(Manifest.Permission.Internet)]

namespace SnnuAndroid;

[Activity(MainLauncher = true, Exported = true)]
public sealed class MainActivity : Activity
{
    private readonly PortalClient _portal = new();
    private readonly CancellationTokenSource _lifetime = new();
    private EditText _account = null!;
    private EditText _password = null!;
    private Spinner _operator = null!;
    private EditText _code = null!;
    private ImageView _image = null!;
    private Button _login = null!;
    private Button _refresh = null!;
    private TextView _status = null!;
    private bool _awaitingCaptcha;

    protected override void OnCreate(Bundle? state)
    {
        base.OnCreate(state);
        var form = new LinearLayout(this) { Orientation = Orientation.Vertical };
        int padding = (int)(20 * Resources!.DisplayMetrics!.Density);
        form.SetPadding(padding, padding, padding, padding);
        _account = new EditText(this) { Hint = "学号", InputType = InputTypes.ClassText };
        _password = new EditText(this)
        {
            Hint = "密码", InputType = InputTypes.ClassText | InputTypes.TextVariationPassword
        };
        _operator = new Spinner(this);
        _operator.Adapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleSpinnerDropDownItem,
            new[] { "校园网直连", "中国移动", "中国联通", "中国电信" });
        _code = new EditText(this) { Hint = "图形验证码", Visibility = ViewStates.Gone };
        _image = new ImageView(this) { ContentDescription = "图形验证码", Visibility = ViewStates.Gone };
        _image.SetAdjustViewBounds(true);
        _login = new Button(this) { Text = "登录" };
        _refresh = new Button(this) { Text = "换一张验证码", Visibility = ViewStates.Gone };
        _status = new TextView(this) { Text = "连接校园 Wi-Fi 后登录。学校门户使用 HTTP。" };
        foreach (View view in new View[] { _account, _password, _operator, _image, _code, _refresh, _login, _status })
        {
            view.SaveEnabled = false;
            form.AddView(view);
        }
        var scroll = new ScrollView(this);
        scroll.AddView(form);
        SetContentView(scroll);
        _login.Click += async (_, _) => await RunAsync(LoginAsync);
        _refresh.Click += async (_, _) => await RunAsync(RefreshCaptchaAsync);
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        _login.Enabled = _refresh.Enabled = false;
        try { await operation(_lifetime.Token); }
        catch (Exception) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { _status.Text = "请求失败，请确认手机已连接校园 Wi-Fi。"; }
        finally
        {
            if (!_lifetime.IsCancellationRequested) _login.Enabled = _refresh.Enabled = true;
        }
    }

    private async Task LoginAsync(CancellationToken token)
    {
        string account = _account.Text?.Trim() ?? "";
        string password = _password.Text ?? "";
        string code = _code.Text?.Trim() ?? "";
        if (account.Length == 0 || password.Length == 0 || (_awaitingCaptcha && code.Length == 0))
        {
            _status.Text = "请填写学号、密码和当前要求的验证码。";
            return;
        }

        _status.Text = "正在登录…";
        var outlet = (Operator)_operator.SelectedItemPosition;
        var result = _awaitingCaptcha
            ? await _portal.LoginAsync(account, password, outlet, code, token)
            : await _portal.LoginAsync(account, password, outlet, token);
        token.ThrowIfCancellationRequested();
        _status.Text = result.Message;
        _awaitingCaptcha = result.NeedCaptcha;
        _code.Visibility = _refresh.Visibility = _image.Visibility =
            _awaitingCaptcha ? ViewStates.Visible : ViewStates.Gone;
        _code.Text = "";
        if (_awaitingCaptcha) await RefreshCaptchaAsync(token);
        else _image.SetImageDrawable(null);
        if (result.Success) _password.Text = "";
    }

    private async Task RefreshCaptchaAsync(CancellationToken token)
    {
        _image.SetImageDrawable(null);
        _code.Text = "";
        var captcha = await _portal.FetchCaptchaAsync(token);
        token.ThrowIfCancellationRequested();
        var bitmap = captcha is null ? null : BitmapFactory.DecodeByteArray(captcha.Bytes, 0, captcha.Bytes.Length);
        _image.SetImageBitmap(bitmap);
        if (bitmap is null) _status.Text = "验证码图片加载失败，请点「换一张验证码」。";
    }

    protected override void OnDestroy()
    {
        _lifetime.Cancel();
        _portal.Dispose();
        base.OnDestroy();
    }
}
