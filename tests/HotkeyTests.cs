using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using CaptionRelay.Input;
using Xunit;
using M = CaptionRelay.Input.HotkeyStateMachine;

namespace CaptionRelay.Tests
{
    public class HotkeyTests
    {
        /// <summary>Simulated keyboard: like GetAsyncKeyState in a hook, the state seen is the state BEFORE each event.</summary>
        private sealed class Keyboard
        {
            private readonly HashSet<int> _down = new HashSet<int>();
            private readonly HotkeyStateMachine _machine;

            public Keyboard(string send = "CtrlShift", string skip = "F9")
            {
                Hotkey.TryParse(send, out Hotkey s);
                Hotkey.TryParse(skip, out Hotkey k);
                _machine = new HotkeyStateMachine(s, k);
            }

            public List<HotkeyAction> Actions { get; } = new List<HotkeyAction>();

            public Keyboard Down(int vk)
            {
                Record(_machine.OnKey(vk, true, IsPressed));
                _down.Add(vk);
                AddGeneric(vk, true);
                return this;
            }

            public Keyboard Up(int vk)
            {
                Record(_machine.OnKey(vk, false, IsPressed));
                _down.Remove(vk);
                AddGeneric(vk, false);
                return this;
            }

            public Keyboard Tap(int vk) => Down(vk).Up(vk);

            public string Result => Actions.Count == 0 ? "none" : string.Join(",", Actions);

            private bool IsPressed(int vk) => _down.Contains(vk);

            private void Record(HotkeyAction a)
            {
                if (a != HotkeyAction.None)
                {
                    Actions.Add(a);
                }
            }

            // GetAsyncKeyState(VK_CONTROL/VK_SHIFT/VK_MENU) reports either side.
            private void AddGeneric(int vk, bool down)
            {
                int generic = vk == M.VK_LCONTROL || vk == M.VK_RCONTROL ? M.VK_CONTROL
                    : vk == M.VK_LSHIFT || vk == M.VK_RSHIFT ? M.VK_SHIFT
                    : vk == 0xA4 || vk == 0xA5 ? M.VK_MENU
                    : 0;
                if (generic == 0)
                {
                    return;
                }

                int other = generic == M.VK_CONTROL ? (vk == M.VK_LCONTROL ? M.VK_RCONTROL : M.VK_LCONTROL)
                    : generic == M.VK_SHIFT ? (vk == M.VK_LSHIFT ? M.VK_RSHIFT : M.VK_LSHIFT)
                    : (vk == 0xA4 ? 0xA5 : 0xA4);
                if (down || _down.Contains(other))
                {
                    _down.Add(generic);
                }
                else
                {
                    _down.Remove(generic);
                }
            }
        }

        private const int LCtrl = M.VK_LCONTROL;
        private const int RCtrl = M.VK_RCONTROL;
        private const int LShift = M.VK_LSHIFT;
        private const int RShift = M.VK_RSHIFT;
        private const int LAlt = 0xA4;
        private const int LWin = M.VK_LWIN;
        private const int T = (int)Keys.T;
        private const int F9 = (int)Keys.F9;
        private const int F24 = (int)Keys.F24;

        [Fact]
        public void CtrlThenShift_ReleasedInAnyOrder_SendsOnce()
        {
            Assert.Equal("Send", new Keyboard().Down(LCtrl).Down(LShift).Up(LShift).Up(LCtrl).Result);
            Assert.Equal("Send", new Keyboard().Down(LShift).Down(LCtrl).Up(LShift).Up(LCtrl).Result);
        }

        [Fact]
        public void RightCtrlWithLeftShift_Sends()
        {
            Assert.Equal("Send", new Keyboard().Down(RCtrl).Down(LShift).Up(RCtrl).Up(LShift).Result);
        }

        [Fact]
        public void FiresOnRelease_NotOnPress()
        {
            var kb = new Keyboard().Down(LCtrl).Down(LShift);
            Assert.Equal("none", kb.Result);
            kb.Up(LShift);
            Assert.Equal("Send", kb.Result);
        }

        [Fact]
        public void CtrlShiftT_DoesNotSend()
        {
            Assert.Equal("none", new Keyboard().Down(LCtrl).Down(LShift).Tap(T).Up(LShift).Up(LCtrl).Result);
        }

        [Fact]
        public void OtherKeyBeforeShift_CancelsUntilBothReleased()
        {
            var kb = new Keyboard().Down(LCtrl).Tap(T).Down(LShift).Up(LShift).Up(LCtrl);
            Assert.Equal("none", kb.Result);
            kb.Down(LCtrl).Down(LShift).Up(LShift).Up(LCtrl);
            Assert.Equal("Send", kb.Result);
        }

        [Fact]
        public void AutoRepeatWhileHeld_SendsOnce()
        {
            var kb = new Keyboard().Down(LCtrl).Down(LShift).Down(LShift).Down(LShift).Down(LCtrl).Up(LShift).Up(LCtrl);
            Assert.Equal("Send", kb.Result);
        }

        [Fact]
        public void ReleaseOneAndPressAgainWhileOtherHeld_DoesNotFireTwice()
        {
            var kb = new Keyboard().Down(LCtrl).Down(LShift).Up(LShift).Down(LShift).Up(LShift).Up(LCtrl);
            Assert.Equal("Send", kb.Result);
        }

        [Fact]
        public void CtrlAloneOrShiftAlone_DoesNothing()
        {
            Assert.Equal("none", new Keyboard().Tap(LCtrl).Tap(LShift).Result);
        }

        [Fact]
        public void WithAltOrWinHeld_DoesNotSend()
        {
            Assert.Equal("none", new Keyboard().Down(LAlt).Down(LCtrl).Down(LShift).Up(LShift).Up(LCtrl).Up(LAlt).Result);
            Assert.Equal("none", new Keyboard().Down(LCtrl).Down(LShift).Tap(LWin).Up(LShift).Up(LCtrl).Result);
        }

        [Fact]
        public void F9Alone_Skips_ButNotWithModifiers()
        {
            Assert.Equal("Skip", new Keyboard().Tap(F9).Result);
            Assert.Equal("none", new Keyboard().Down(LCtrl).Tap(F9).Up(LCtrl).Result);
            Assert.Equal("none", new Keyboard().Down(LShift).Tap(F9).Up(LShift).Result);
        }

        [Fact]
        public void F9AutoRepeat_SkipsOnce()
        {
            Assert.Equal("Skip", new Keyboard().Down(F9).Down(F9).Down(F9).Up(F9).Result);
        }

        [Fact]
        public void TwoSeparatePresses_SendTwice()
        {
            var kb = new Keyboard().Down(LCtrl).Down(LShift).Up(LShift).Up(LCtrl).Down(LCtrl).Down(LShift).Up(LCtrl).Up(LShift);
            Assert.Equal("Send,Send", kb.Result);
        }

        [Fact]
        public void SingleKeySendAndCtrlShiftSkip_AreConfigurable()
        {
            var kb = new Keyboard(send: "F24", skip: "CtrlShift");
            kb.Tap(F24).Down(LCtrl).Down(LShift).Up(LShift).Up(LCtrl);
            Assert.Equal("Send,Skip", kb.Result);
        }

        [Fact]
        public void NoneDisables()
        {
            Assert.Equal("none", new Keyboard(send: "None", skip: "None").Down(LCtrl).Down(LShift).Up(LShift).Up(LCtrl).Tap(F9).Result);
        }

        [Theory]
        [InlineData("CtrlShift", "Ctrl+Shift")]
        [InlineData("ctrl+shift", "Ctrl+Shift")]
        [InlineData("F9", "F9")]
        [InlineData("pause", "Pause")]
        [InlineData("None", "None")]
        [InlineData("", "None")]
        public void Parse_Valid(string text, string expected)
        {
            Assert.True(Hotkey.TryParse(text, out Hotkey hotkey));
            Assert.Equal(expected, hotkey.ToString());
        }

        [Theory]
        [InlineData("Foo")]
        [InlineData("120")]
        [InlineData("F8, Control")]
        [InlineData("ControlKey")]
        [InlineData("LShiftKey")]
        public void Parse_Invalid(string text)
        {
            Assert.False(Hotkey.TryParse(text, out _));
        }
    }
}
