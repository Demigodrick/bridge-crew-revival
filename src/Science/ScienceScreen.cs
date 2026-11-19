using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace StbcRevival.Science
{
    /// <summary>
    /// The science console's touchscreen: a world-space canvas on SciencePlacement.ScreenAnchor, ScreenSize big, built
    /// from copies of the helm's own widgets (ConsoleWidgets: the IMPULSE button for buttons, the throttle for sliders,
    /// the IMPULSE button's text for text), so VR pokes and drags and the desktop mouse work as at a seat. One per
    /// bridge load: made while the anchor is there and the console is on (ScienceSession.Active), gone with either.
    ///
    ///   SCIENCE                                   (status)
    ///   Targets: "In range:" and up to 5 rows (name, distance); tap one to scan it.
    ///   Scan:    two mini game panels side by side (picture, readout, three sliders), the shared SCAN bar, BACK.
    ///   Out of range / Busy: one line in the middle.
    ///   Upgrades: ...                             (always)
    ///
    /// What the screen does is ConsoleFlow's (shared, unit-tested); this renders it and passes taps and slider moves on.
    /// Being within reach (registering the controls with the hands) is ScienceUse's: it calls Register/Unregister and
    /// Show/Hide.
    /// </summary>
    internal sealed class ScienceScreen : IScienceLink
    {
        private const string P = "Science console: ";
        internal const float Units = 1000f;                  // the canvas is this many units wide
        internal const float TitleFont = 34f, BodyFont = 26f, SmallFont = 20f;
        private const int Rows = 5;
        private const float TargetsEvery = 0.5f, ClaimantEvery = 0.1f, ActorEvery = 1f, TextEvery = 0.25f, DrawEvery = 0.05f, KeepEvery = 0.25f;

        // ------------------------------------------------------------------------------------------- one per bridge
        internal static ScienceScreen Current { get; private set; }
        private static Transform failedFor;
        private static int failures;
        private static float nextTry;

        /// <summary>Every frame: builds the screen when there's an anchor and the console is on, takes it down when not.</summary>
        internal static void Update()
        {
            var anchor = SciencePlacement.ScreenAnchor;
            bool want = anchor != null && ScienceSession.Active;
            if (Current != null && (!want || Current.anchor != anchor || Current.root == null))
            {
                Current.Destroy();
                Current = null;
            }
            if (Current == null && want && Time.unscaledTime >= nextTry)
            {
                try
                {
                    Current = new ScienceScreen(anchor);
                    failedFor = null;
                    failures = 0;
                }
                catch (Exception e)
                {
                    // The helm may not be loaded yet: soon again, then less and less often (up to once a minute).
                    if (failedFor != anchor) { failedFor = anchor; failures = 0; Plugin.Log.LogWarning(P + "making the screen: " + e.Message + " (trying again)"); }
                    nextTry = Time.unscaledTime + Mathf.Min(60f, 2f * (1 << Math.Min(failures, 5)));
                    failures++;
                }
            }
            Current?.Tick(Time.unscaledDeltaTime);
        }

        // ------------------------------------------------------------------------------------------- the screen
        private readonly Transform anchor;
        private readonly GameObject root, gridObject;
        private readonly ConsoleFlow flow;
        private readonly List<HandInteraction> interactions = new List<HandInteraction>();
        private readonly GameObject targetsPage, scanPage, messagePage, panelsBox;
        private readonly TMPro.TMP_Text status, header, empty, message, scanValue, upgrades;
        private readonly ConsoleButton[] rows = new ConsoleButton[Rows];
        private readonly string[] rowIds = new string[Rows];
        private readonly MiniGamePanel[] panels = new MiniGamePanel[2];
        private readonly RectTransform barFill;

        private bool visible = true;
        private ScreenMode shownMode = (ScreenMode)(-1);
        private MiniGame boundA;
        private IList<ScienceTarget> targets = new List<ScienceTarget>();
        private TargetState targetState = TargetState.Missing;
        private int claimant, localActor = 1, busyActor;
        private string busyName = "";
        private float nextTargets, nextClaimant, nextActor, nextText, nextDraw, nextKeep, nextBusyName, shownBar = -1f;

        private ScienceScreen(Transform anchor)
        {
            this.anchor = anchor;
            var helm = ConsoleWidgets.FindHelm();
            var impulse = helm != null ? ConsoleWidgets.ImpulseButton(helm) : null;
            if (impulse == null) throw new InvalidOperationException("no helm console to copy the controls from yet");
            Vector2 size = SciencePlacement.ScreenSize;
            if (size.x <= 0f || size.y <= 0f) throw new InvalidOperationException("the placement gives the screen no size");

            // Built inactive: the copies are made safe before they wake up.
            root = new GameObject("Science Screen", typeof(RectTransform));
            root.SetActive(false);
            root.layer = impulse.layer;
            var canvasRect = (RectTransform)root.transform;
            canvasRect.SetParent(anchor, false);
            canvasRect.localPosition = new Vector3(0f, 0f, -0.002f); // just in front of the console's surface
            canvasRect.localRotation = Quaternion.identity;
            float height = Units * size.y / size.x;
            canvasRect.sizeDelta = new Vector2(Units, height);
            canvasRect.localScale = Vector3.one * (size.x / Units);
            root.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            try
            {
                var grid = ConsoleWidgets.MakeGrid(canvasRect);
                gridObject = grid is Component g ? g.gameObject : null;

                Box(canvasRect, "Back", 0f, 0f, 1f, 1f).gameObject.AddComponent<Image>().color = new Color32(7, 20, 34, 240);
                SetText(ConsoleWidgets.CloneLabel(Box(canvasRect, "Title", 0.03f, 0.9f, 0.5f, 0.98f), TitleFont), Translation.T("SCIENCE"));
                status = Aligned(ConsoleWidgets.CloneLabel(Box(canvasRect, "Status", 0.5f, 0.9f, 0.97f, 0.98f), BodyFont), "Right");
                upgrades = ConsoleWidgets.CloneLabel(Box(canvasRect, "Upgrades", 0.03f, 0.02f, 0.97f, 0.09f), BodyFont);
                upgrades.BMAIEFHKPMA = true;      // enableAutoSizing: a long line shrinks to fit
                upgrades.LOBPGECEJCD = BodyFont;  // fontSizeMax
                upgrades.CNPBHPONNJK = SmallFont * 0.7f; // fontSizeMin

                // Targets
                var page = Box(canvasRect, "Targets", 0.03f, 0.11f, 0.97f, 0.89f);
                targetsPage = page.gameObject;
                header = ConsoleWidgets.CloneLabel(Box(page, "Header", 0f, 0.88f, 1f, 1f), BodyFont);
                empty = ConsoleWidgets.CloneLabel(Box(page, "Empty", 0f, 0.7f, 1f, 0.82f), BodyFont);
                for (int i = 0; i < Rows; i++)
                {
                    float top = 0.85f - i * 0.17f;
                    int index = i;
                    rows[i] = ConsoleWidgets.CloneButton(Box(page, "Row " + i, 0f, top - 0.14f, 1f, top), "", () => Tap(index), grid);
                    foreach (var c in rows[i].Texts) RowText(rows[i].Face.transform, c);
                }

                // Scan
                page = Box(canvasRect, "Scan", 0.03f, 0.11f, 0.97f, 0.89f);
                scanPage = page.gameObject;
                panelsBox = Box(page, "Games", 0f, 0.24f, 1f, 1f).gameObject;
                panels[0] = new MiniGamePanel(Box(panelsBox.transform, "Game A", 0f, 0f, 0.48f, 1f), grid);
                panels[1] = new MiniGamePanel(Box(panelsBox.transform, "Game B", 0.52f, 0f, 1f, 1f), grid);
                SetText(ConsoleWidgets.CloneLabel(Box(page, "Scan Label", 0f, 0.1f, 0.12f, 0.2f), BodyFont), Translation.T("SCAN"));
                var bar = Box(page, "Bar", 0.13f, 0.12f, 0.6f, 0.18f);
                bar.gameObject.AddComponent<Image>().color = new Color32(13, 35, 55, 255);
                barFill = Box(bar, "Fill", 0f, 0f, 0f, 1f);
                barFill.gameObject.AddComponent<Image>().color = new Color32(79, 179, 255, 255);
                scanValue = ConsoleWidgets.CloneLabel(Box(page, "Scan Value", 0.62f, 0.1f, 0.8f, 0.2f), BodyFont);
                ConsoleWidgets.CloneButton(Box(page, "Back Button", 0.82f, 0.02f, 1f, 0.2f), Translation.T("BACK"), () => flow.Leave(), grid);

                // Out of range / busy
                messagePage = Box(canvasRect, "Message", 0.05f, 0.35f, 0.95f, 0.65f).gameObject;
                message = Aligned(ConsoleWidgets.CloneLabel(messagePage.transform, TitleFont), "Center");
                message.GADKGKMKHCE = true; // enableWordWrapping

                flow = new ConsoleFlow(this);
                root.SetActive(true);
                interactions.AddRange(root.GetComponentsInChildren<HandInteraction>(true));
                Plugin.Log.LogInfo(P + $"screen made on {anchor.parent?.name}: {size.x:0.##} x {size.y:0.##} m, {interactions.Count} control(s), "
                    + (grid != null ? "its own hand grid" : "the helm's hand poses"));
            }
            catch
            {
                foreach (var p in panels) p?.Destroy();
                UnityEngine.Object.Destroy(root);
                if (gridObject != null) UnityEngine.Object.Destroy(gridObject);
                throw;
            }
        }

        // ------------------------------------------------------------------------------------------- for ScienceUse
        /// <summary>The screen's controls, for ConsoleWidgets.Register/Unregister.</summary>
        internal IList<HandInteraction> Interactions => interactions;

        internal ScreenMode Mode => flow.Mode;

        /// <summary>The local player holds the console (is scanning, or paused out of range).</summary>
        internal bool InUse => flow.InUse(claimant, localActor);

        internal void Show() { visible = true; if (root != null && !root.activeSelf) root.SetActive(true); }
        internal void Hide() { visible = false; if (root != null && root.activeSelf) root.SetActive(false); }

        /// <summary>Back to the list, the scan's progress reported and the console freed (the player left it).</summary>
        internal void Leave() => flow.Leave();

        // ------------------------------------------------------------------------------------------- every frame
        internal void Tick(float dt)
        {
            float now = Time.unscaledTime;
            if (now >= nextActor) { nextActor = now + ActorEvery; localActor = ScienceSession.LocalActor; }
            if (now >= nextClaimant) { nextClaimant = now + ClaimantEvery; claimant = ScienceSession.Claimant; }
            if (now >= nextTargets) RefreshTargets(now);

            string id = flow.TargetId;
            flow.Tick(now, dt, claimant, localActor, targetState, id != null && ScienceSession.Board.IsDone(id));
            if (flow.TargetId != id) RefreshTargets(now); // a scan began or ended: the list and state again

            if (now >= nextKeep) { nextKeep = now + KeepEvery; ConsoleWidgets.KeepRegistered(); }
            if (!visible) return;
            Render(now);
        }

        private void RefreshTargets(float now)
        {
            nextTargets = now + TargetsEvery;
            targets = ScienceTargets.InRange(float.PositiveInfinity);
            targetState = TargetState.Missing;
            string id = flow.TargetId;
            if (id == null) return;
            foreach (var t in targets)
                if (t.Id == id) { targetState = t.Distance <= ScienceTuning.RangeMetres ? TargetState.InRange : TargetState.OutOfRange; break; }
        }

        private void Tap(int row)
        {
            string id = rowIds[row];
            if (id == null) return;
            var board = ScienceSession.Board;
            if (flow.Start(id, board.Progress(id), board.IsDone(id), Time.unscaledTime)) RefreshTargets(Time.unscaledTime);
        }

        // ------------------------------------------------------------------------------------------- drawing
        private void Render(float now)
        {
            ScreenMode mode = flow.Mode;
            if (mode != shownMode)
            {
                shownMode = mode;
                Active(targetsPage, mode == ScreenMode.Targets);
                Active(scanPage, mode == ScreenMode.Scan || mode == ScreenMode.OutOfRange);
                Active(panelsBox, mode == ScreenMode.Scan);
                Active(messagePage, mode == ScreenMode.OutOfRange || mode == ScreenMode.Busy);
                nextText = 0f;
            }
            if (flow.GameA != boundA)
            {
                boundA = flow.GameA;
                panels[0].Bind(flow.GameA);
                panels[1].Bind(flow.GameB);
            }
            if (mode == ScreenMode.Scan && now >= nextDraw)
            {
                nextDraw = now + DrawEvery;
                panels[0].Draw(now);
                panels[1].Draw(now);
            }
            if (flow.Progress != null && Mathf.Abs(flow.Progress.Value - shownBar) > 0.05f)
            {
                shownBar = flow.Progress.Value;
                barFill.anchorMax = new Vector2(shownBar / 100f, 1f);
            }
            if (now < nextText) return;
            nextText = now + TextEvery;
            Texts(mode, now);
        }

        private void Texts(ScreenMode mode, float now)
        {
            SetText(upgrades, ConsoleText.Upgrades(ScienceSession.Ledger, Translation.T));
            SetText(status, InUse ? Translation.T("In use") : "");
            switch (mode)
            {
                case ScreenMode.Targets:
                    SetText(header, Translation.T("In range:"));
                    int n = 0;
                    bool comma = Translation.German;
                    foreach (var t in targets)
                    {
                        if (n >= Rows) break;
                        if (t.Distance > ScienceTuning.RangeMetres) continue;
                        rowIds[n] = t.Id;
                        rows[n].SetLabel(Translation.T(t.Name ?? t.Id) + "   " + ConsoleText.Km(t.Distance, comma) + " km");
                        Active(rows[n].gameObject, true);
                        n++;
                    }
                    for (int i = n; i < Rows; i++) { rowIds[i] = null; Active(rows[i].gameObject, false); }
                    SetText(empty, n == 0 ? Translation.T("No targets in range") : "");
                    break;
                case ScreenMode.Scan:
                case ScreenMode.OutOfRange:
                    panels[0].UpdateText();
                    panels[1].UpdateText();
                    var p = flow.Progress;
                    SetText(scanValue, p == null ? "" : Mathf.FloorToInt(p.Value) + "%  " + Translation.T(p.Locked ? "LOCKED" : "no lock"));
                    if (mode == ScreenMode.OutOfRange)
                        SetText(message, string.Format(Translation.T("Out of range: helm, close to {0} km"), ConsoleText.Km(ScienceTuning.RangeMetres, Translation.German)));
                    break;
                case ScreenMode.Busy:
                    if (flow.BusyActor != busyActor || now >= nextBusyName)
                    {
                        busyActor = flow.BusyActor;
                        nextBusyName = now + 2f;
                        busyName = ScienceSession.PlayerName(busyActor);
                    }
                    SetText(message, string.Format(Translation.T("In use by {0}"), busyName));
                    break;
            }
        }

        // ------------------------------------------------------------------------------------------- IScienceLink
        public void Claim() => ScienceSession.SendClaim();
        public void Release() => ScienceSession.SendRelease();
        public void Heartbeat() => ScienceSession.SendHeartbeat();
        public void Report(string id, float progress) => ScienceSession.SendReport(id, progress);
        public void Complete(string id) => ScienceSession.SendComplete(id);

        // ------------------------------------------------------------------------------------------- going
        private void Destroy()
        {
            try { if (ScienceSession.Active) flow.Leave(); } catch (Exception e) { Plugin.Log.LogWarning(P + "leaving: " + e.Message); }
            ConsoleWidgets.Unregister(interactions);
            foreach (var p in panels) p?.Destroy();
            if (root != null) UnityEngine.Object.Destroy(root);
            if (gridObject != null) UnityEngine.Object.Destroy(gridObject);
            Plugin.Log.LogInfo(P + "screen taken down");
        }

        // ------------------------------------------------------------------------------------------- layout helpers
        /// <summary>A box in a parent, by fractions of it (x left to right, y bottom to top).</summary>
        internal static RectTransform Box(Transform parent, string name, float x0, float y0, float x1, float y1)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            var r = (RectTransform)go.transform;
            r.SetParent(parent, false);
            r.anchorMin = new Vector2(x0, y0);
            r.anchorMax = new Vector2(x1, y1);
            r.pivot = new Vector2(0.5f, 0.5f);
            r.offsetMin = r.offsetMax = Vector2.zero;
            r.localRotation = Quaternion.identity;
            r.localScale = Vector3.one;
            return r;
        }

        /// <summary>A text, set only when it changes (TMP lays it out again on every set).</summary>
        internal static void SetText(TMPro.TMP_Text tmp, string text)
        {
            if (tmp != null && tmp.OIKALDNFGKE != text) tmp.OIKALDNFGKE = text; // TMP_Text.text
        }

        private static TMPro.TMP_Text Aligned(TMPro.TMP_Text tmp, string how)
        {
            try { tmp.AEDOGHDAPML = (TMPro.EGJGCPILBBH)Enum.Parse(typeof(TMPro.EGJGCPILBBH), how); } // alignment
            catch (Exception) { }
            return tmp;
        }

        // A row's text across the whole face (a little in from its left edge), left aligned, on one line.
        private static void RowText(Transform face, Component c)
        {
            if (c.transform is RectTransform r && face is RectTransform f)
            {
                r.SetParent(f, false);
                ConsoleWidgets.Stretch(r);
                r.localRotation = Quaternion.identity;
                r.localScale = Vector3.one;
                r.offsetMin = new Vector2(f.rect.width * 0.03f, 0f);
                r.offsetMax = new Vector2(-f.rect.width * 0.03f, 0f);
            }
            if (c is TMPro.TMP_Text tmp)
            {
                tmp.GADKGKMKHCE = false; // enableWordWrapping
                Aligned(tmp, "Left");
            }
        }

        private static void Active(GameObject go, bool on)
        {
            if (go != null && go.activeSelf != on) go.SetActive(on);
        }
    }
}
