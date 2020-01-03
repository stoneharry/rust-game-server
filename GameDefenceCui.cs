using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using Oxide.Plugins;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("GameDefenceCui", "stoneharry", "0.0.1")]
    class GameDefenceCui : RustPlugin
    {
        private const string _MainElementName = "GameDefenceMainPanel";
        private const string _ProgressBarElementName = "GameDefenceProgessBar";
        private const double _ScoreBarWidth = 0.2;
        private const double _ScoreBarHeight = 0.04;
        private const double _TopBorder = 0.05;
        private const double _ProgressBarHeight = 0.01;
        private const double _BorderAboveProgressBar = 0.005;

        #region PublicAPI
        void DisplayGUIExternal(BasePlayer player, int secondsLeft, string message, double progress = 0) => DisplayGUI(player, secondsLeft, message, progress);

        void DestroyGUIExternal(BasePlayer player) => DestroyGUI(player);
        #endregion

        #region GuiMethods
        private void DisplayGUI(BasePlayer player, int secondsLeft, string message = "", double progress = 0)
        {
            DestroyGUI(player);
            CuiHelper.AddUi(player, CreateGUI(secondsLeft, message));
            if (progress > 0)
            {
                CuiHelper.AddUi(player, CreateProgressElement(progress));
            }
        }

        private void DestroyGUI(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, _MainElementName);
            CuiHelper.DestroyUi(player, _ProgressBarElementName);
        }

        private CuiElementContainer CreateGUI(int secondsLeft, string message)
        {
            var gui = new CuiElementContainer();

            double left = (1.0 - _ScoreBarWidth) / 2;
            double bottom = 1.0 - _TopBorder - _ScoreBarHeight;

            double anchorMinX = left;
            double anchorMinY = bottom;
            double anchorMaxX = left + _ScoreBarWidth;
            double anchorMaxY = bottom + _ScoreBarHeight;

            gui.Add(new CuiElement
            {
                Name = _MainElementName,
                Components =
                {
                    new CuiImageComponent { Color = "0 0 0 0.8"},
                    new CuiRectTransformComponent { AnchorMin = $"{anchorMinX} {anchorMinY}", AnchorMax = $"{anchorMaxX} {anchorMaxY}" }
                }
            });

            gui.Add(CreateTimeElement(_MainElementName, secondsLeft));
            gui.Add(CreateObjectiveElement(_MainElementName, message));
            return gui;
        }

        private CuiElement CreateTimeElement(string parentName, int secondsLeft)
        {
            double width = 0.12;
            string timeLeft = $"{secondsLeft / 60}:{(secondsLeft % 60):D2}";
            double left = (1.0 - width)/2;
            double anchorMinX = left;
            double anchorMinY = 0;
            double anchorMaxX = left + width;
            double anchorMaxY = 1;
 
            return new CuiElement()
            {
                Name = CuiHelper.GetGuid(),
                Parent = parentName,
                Components = {
                    new CuiTextComponent
                    {
                        Text = timeLeft,
                        Align = TextAnchor.UpperCenter,
                        FontSize = 15,
                    },
                    new CuiRectTransformComponent { AnchorMin = $"{anchorMinX} {anchorMinY}", AnchorMax = $"{anchorMaxX} {anchorMaxY}" }
                }
            };
        }

        private CuiElement CreateObjectiveElement(string parentName, string objectiveText)
        {
            double width = 1;
            double left = (1.0 - width) / 2;
            double anchorMinX = left;
            double anchorMinY = 0;
            double anchorMaxX = left + width;
            double anchorMaxY = 1;

            return new CuiElement()
            {
                Name = CuiHelper.GetGuid(),
                Parent = parentName,
                Components = {
                    new CuiTextComponent
                    {
                        Text = objectiveText,
                        Align = TextAnchor.LowerCenter,
                        FontSize = 13,
                    },
                    new CuiRectTransformComponent { AnchorMin = $"{anchorMinX} {anchorMinY}", AnchorMax = $"{anchorMaxX} {anchorMaxY}" }
                }
            };
        }

        private CuiElementContainer CreateProgressElement(double percentage)
        {
            double top = 1.0 - _TopBorder - _ScoreBarHeight - _BorderAboveProgressBar;
            double bottom = top - _ProgressBarHeight;
            double barLength = 1.0;
            double leftBarLength = (barLength * percentage) / 100;

            //backing
            var NewElement = new CuiElementContainer();
            NewElement.Add(new CuiElement
            {
                Name = _ProgressBarElementName,
                Components =
                {
                    new CuiImageComponent { Color = "0 0 0 0.8"},
                    new CuiRectTransformComponent { AnchorMin = $"0.35 {bottom}", AnchorMax = $"0.65 {top}" }
                }
            });
            NewElement.Add(new CuiElement
            {
                Name = CuiHelper.GetGuid(),
                Parent = _ProgressBarElementName,
                Components =
                {
                    new CuiImageComponent { Color = "1 0 0 0.8"},
                    new CuiRectTransformComponent { AnchorMin = $"0.0 0.3", AnchorMax = $"{leftBarLength} 0.7" }
                }
            });
            //right half
            NewElement.Add(new CuiElement
            {
                Name = CuiHelper.GetGuid(),
                Parent = _ProgressBarElementName,
                Components =
                {
                    new CuiImageComponent { Color = "0 0 0 0.8"},
                    new CuiRectTransformComponent { AnchorMin = $"{leftBarLength} 0.3", AnchorMax = $"1.0 0.7" }
                }
            });
            return NewElement;
        }
        #endregion

        #region HelperMethods
        private static string NumberToString(int number, bool isCaps)
        {
            char c = (char)((isCaps ? 66 : 98) + (number - 1));
            return c.ToString();
        }
        #endregion
    }
}
