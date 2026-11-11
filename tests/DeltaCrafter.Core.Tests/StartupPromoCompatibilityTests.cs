using System.Reflection;
using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class StartupPromoCompatibilityTests
{
    [Theory]
    [InlineData("空 格 继 续","前 往 获 取",true)]
    [InlineData("继续","前往获取",true)]
    [InlineData("继续","购买",false)]
    [InlineData("确认重连","返回登录",false)]
    [InlineData("","前往获取",false)]
    [InlineData("继续","",false)]
    public void Continue_is_only_for_confirmed_startup_advert(string continuation,string acquire,bool expected)
    {
        var method=typeof(ScreenProbe).GetMethod("IsStartupPromoText",BindingFlags.NonPublic|BindingFlags.Static);
        Assert.True(method is not null,"尚未识别启动广告的空格继续提示");
        Assert.Equal(expected,(bool)method!.Invoke(null,new object[]{continuation,acquire})!);
    }
}
