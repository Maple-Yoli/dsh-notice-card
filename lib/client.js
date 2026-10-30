// Settings page for dsh-notice-card: a whole "通知卡" section in the settings
// sidebar. Every field is declared `.volatile()` in lib/index.mjs, so an edit
// lands in the running host without a restart: the host wraps each value in a
// `{ get() }` box and the notifier unwraps it on every read.
//
// Loaded by the DSH client module loader, which is why this file is a plain
// CommonJS-style factory instead of an ES module.
window.__ModuleLoader__.load({
  id: "dsh-notice-card",
  factory: (require) => {
    var module = { exports: {} };
    var exports = module.exports;
    Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });

    const react = require("react");
    const h = react.createElement;

    // The profile entry id from cordis.patch.yml, not the package name. This is
    // the key configForms is addressed by.
    const NS = "notice-card";

    // Only "slots" is required up front. configForms is waited for inside apply
    // because it is not present on every host build.
    const inject = ["slots"];

    const FIELDS = {
      onTurnEnd: true,
      onApproval: true,
      onQuestion: true,
      corner: "auto",
      maxStack: 3,
      fontSize: 100,
      seconds: 10,
      theme: "auto",
      animations: "follow",
      scale: "auto",
      language: "auto",
      contrast: "auto",
    };

    // Volatile config arrives as { get() }. Read through it every time.
    const read = (value) =>
      value !== null && typeof value === "object" && typeof value.get === "function"
        ? value.get()
        : value;

    const UNAVAILABLE = Object.freeze({ status: "unavailable", value: null });

    // ---- config transport -------------------------------------------------

    let form = null;
    let detach = null;
    let snapshot = UNAVAILABLE;
    const watchers = new Set();
    // A field the user just touched, held here until the host agrees with it.
    // Without this the control shows the old value until the write has travelled
    // to the host, been stored, and come back through the subscription - half a
    // second in which the select looks like it ignored the click.
    let draft = {};

    function notify() {
      // React only repaints when the value it subscribes to changes identity, and
      // a draft lives outside the host snapshot: without a fresh object here a
      // dragged slider moved the thumb but left the number beside it - and the
      // card - waiting for the config write to come back. A two-field clone per
      // frame costs nothing and is what keeps the controls and the preview on the
      // same value as the pointer.
      if (snapshot !== UNAVAILABLE) snapshot = Object.assign({}, snapshot);
      for (const fn of watchers) {
        try {
          fn();
        } catch {}
      }
    }

    const liveOf = (state, field) => {
      if (!state || state.status !== "ready" || !state.value) return undefined;
      return read(state.value[field]);
    };

    function attach(next) {
      if (detach) {
        try {
          detach();
        } catch {}
        detach = null;
      }
      draft = {};
      form = next && typeof next.getSnapshot === "function" ? next : null;
      if (!form) {
        snapshot = UNAVAILABLE;
        notify();
        return;
      }
      const pull = () => {
        try {
          snapshot = form.getSnapshot();
        } catch {
          snapshot = UNAVAILABLE;
        }
        // A draft that the host now reports back is finished: keeping it would
        // pin the control to a value the host has already stored and hide any
        // later change made from somewhere else.
        for (const field of Object.keys(draft)) {
          if (liveOf(snapshot, field) === draft[field]) delete draft[field];
        }
        notify();
      };
      pull();
      const off = form.subscribe(pull);
      detach = typeof off === "function" ? off : null;
    }

    function subscribe(fn) {
      watchers.add(fn);
      return () => watchers.delete(fn);
    }

    const getSnapshot = () => snapshot;

    function commit(field, value) {
      const target = form;
      if (!target) return;
      draft[field] = value;
      notify();
      const reject = () => {
        delete draft[field];
        notify();
      };
      try {
        const result = target.set(field, value);
        if (result && typeof result.catch === "function") result.catch(reject);
      } catch {
        reject();
      }
    }

    // A slider fires on every pixel of the drag, and each of those would be a config
    // write across the IPC boundary. The draft above already drives the preview, so a
    // drag only holds its value there and the write happens once the reader lets go -
    // or once the drag has been still for a moment, which covers letting go outside
    // the control. Without this the write queue is what makes a drag feel notchy.
    let releaseTimer = null;
    let releasing = {};
    let paintId = 0;
    // The draft lands on every event, but the page is redrawn at most once per
    // frame: a pointer can report more moves than the screen has frames, and a
    // re-render per report is what puts the number beside the thumb behind it.
    function paint() {
      paintId = 0;
      notify();
    }

    function hold(field, value) {
      draft[field] = value;
      if (typeof requestAnimationFrame === "function") {
        if (!paintId) paintId = requestAnimationFrame(paint);
      } else {
        notify();
      }
      releasing[field] = true;
      if (releaseTimer) clearTimeout(releaseTimer);
      releaseTimer = setTimeout(flush, 320);
    }

    function flush() {
      if (paintId && typeof cancelAnimationFrame === "function") {
        cancelAnimationFrame(paintId);
        paintId = 0;
      }
      if (releaseTimer) {
        clearTimeout(releaseTimer);
        releaseTimer = null;
      }
      const fields = Object.keys(releasing);
      releasing = {};
      for (const field of fields) {
        if (Object.prototype.hasOwnProperty.call(draft, field)) commit(field, draft[field]);
      }
    }

    // ---- colour model -----------------------------------------------------

    // Mirrors the palette block in lib/card.cs. There is exactly one pair of
    // colours - black on white and white on black - and `theme` picks it. The
    // chips below the contrast slider are computed from the same functions, so
    // what they show is what the card draws.
    const channel = (hex, index) => parseInt(hex.slice(1 + index * 2, 3 + index * 2), 16);

    const mix = (a, b, t) => {
      const k = Math.max(0, Math.min(1, t));
      const part = (index) =>
        Math.round(channel(a, index) + (channel(b, index) - channel(a, index)) * k);
      return (
        "#" +
        [0, 1, 2].map((index) => part(index).toString(16).padStart(2, "0")).join("").toUpperCase()
      );
    };

    const shift = (hex, amount) => mix(hex, amount >= 0 ? "#FFFFFF" : "#000000", Math.abs(amount));

    // Contrast is a continuum, not three boxes: a slider has to mean something at
    // every notch, so the same number drives the text shift and the border weight
    // in both this page and card.cs. 50 is the untouched card, 100 the strongest
    // separation, 0 the softest. The old words still read as their ends.
    function contrastRank(value) {
      const text = String(value === undefined || value === null ? "" : value).trim();
      if (text === "" || text === "auto") return 0;
      if (text === "high") return 1;
      if (text === "soft") return -1;
      const number = Number(text);
      if (!Number.isFinite(number)) return 0;
      return (Math.max(0, Math.min(100, number)) - 50) / 50;
    }

    // The slider works in whole percent; the stored form is still a string, so a
    // config that predates the slider keeps meaning what it meant.
    function contrastPercent(value) {
      return Math.round(50 + contrastRank(value) * 50);
    }

    function scalePercent(value) {
      const text = String(value === undefined || value === null ? "" : value).trim();
      const number = Number(text);
      if (text === "" || text === "auto" || !Number.isFinite(number) || number <= 0) return 100;
      const percent = number < 10 ? number * 100 : number;
      return Math.round(Math.max(60, Math.min(200, percent)));
    }

    function systemDark() {
      try {
        if (document.body && document.body.matches("[data-ds-dark-theme], [data-theme=dark]")) {
          return true;
        }
        if (document.documentElement && document.documentElement.dataset.theme === "dark") {
          return true;
        }
        return window.matchMedia("(prefers-color-scheme: dark)").matches;
      } catch {
        return false;
      }
    }

    function palette(values) {
      const deep = values.theme === "dark" || (values.theme !== "light" && systemDark());
      const surface = deep ? "#000000" : "#FFFFFF";
      const title = deep ? "#FFFFFF" : "#000000";
      let body = deep ? "#D6D6D6" : "#2E2E2E";
      let muted = deep ? "#8C8C8C" : "#757575";
      const rank = contrastRank(values.contrast);
      if (rank > 0) {
        body = mix(body, title, 0.75 * rank);
        muted = mix(muted, title, 0.45 * rank);
      } else if (rank < 0) {
        body = shift(body, (deep ? -0.16 : 0.16) * -rank);
        muted = shift(muted, (deep ? -0.16 : 0.16) * -rank);
      }
      return { surface, title, body, muted };
    }

    // ---- desktop preview --------------------------------------------------

    // The card is drawn by a separate process, so the page cannot render it. It
    // asks the host instead: the same origin serves the route, the values shown
    // above are the payload, and the host hands them to one long-lived preview
    // process, which redraws the whole stack. Nothing here is awaited - a preview
    // that fails must not block a control.
    const PREVIEW_PATH = "/api/dsh-notice-card-preview";

    function postPreview(payload) {
      try {
        const result = fetch(PREVIEW_PATH, {
          method: "POST",
          headers: { "content-type": "application/json" },
          body: JSON.stringify(payload),
        });
        if (result && typeof result.catch === "function") result.catch(() => {});
      } catch {}
    }

    // One post out at a time, and the next one always carries whatever is newest
    // at that moment. This is what keeps the card and the thumb together: a drag
    // reports faster than the host can act, so a fixed interval either posted a
    // value the thumb had already left or made the last value of the drag wait out
    // the rest of the interval after the thumb had stopped. Here the reply
    // releases the next post, so the value that ends the drag goes out at once and
    // the card stops moving when the thumb does.
    let previewPending = null;
    let previewFlight = null;
    let previewBusy = false;
    let previewClosed = false;

    function sendPreview() {
      if (previewBusy || previewClosed) return;
      const payload = previewPending;
      previewPending = null;
      if (!payload) return;
      previewBusy = true;
      let settled = false;
      // A request that never settles must not wedge the preview for good. The host
      // answers in a couple of milliseconds - it writes a small file and returns -
      // so this only ever fires on a request that is not coming back.
      const stall = setTimeout(release, 250);
      function release() {
        if (settled) return;
        settled = true;
        clearTimeout(stall);
        previewBusy = false;
        previewFlight = null;
        if (previewPending && !previewClosed) sendPreview();
      }
      try {
        const result = fetch(PREVIEW_PATH, {
          method: "POST",
          headers: { "content-type": "application/json" },
          body: JSON.stringify(payload),
        });
        if (result && typeof result.then === "function") {
          previewFlight = result;
          result.then(release, release);
        } else {
          release();
        }
      } catch {
        release();
      }
    }

    function schedulePreview(values) {
      // A page that was closed and opened again is a new preview, not the old
      // one still refusing to post.
      previewClosed = false;
      previewPending = values;
      sendPreview();
    }

    // The close has to travel after the preview that is already out, or it can be
    // overtaken: the spec file would be recreated by a post written before the
    // close but delivered after it, and the stack would come back up with nothing
    // on the page left to take it down again.
    function closePreview() {
      previewClosed = true;
      previewPending = null;
      const send = () => postPreview({ close: true });
      const flight = previewFlight;
      if (!flight) {
        send();
        return;
      }
      flight.then(send, send);
    }

    // ---- controls ---------------------------------------------------------

    function Row(props) {
      return h(
        "div",
        { className: "nc-row" },
        h("div", { className: "nc-label" }, props.label),
        h("div", { className: "nc-control" }, props.children),
        h("div", { className: "nc-hint" }, props.hint),
      );
    }

    function Select(props) {
      return h(
        "select",
        {
          className: "nc-select",
          value: props.value,
          disabled: props.disabled,
          onChange: (event) => props.onChange(event.target.value),
        },
        props.options.map((option) => h("option", { key: option[0], value: option[0] }, option[1])),
      );
    }

    function Toggle(props) {
      return h("input", {
        className: "nc-check",
        type: "checkbox",
        checked: Boolean(props.checked),
        disabled: props.disabled,
        onChange: (event) => props.onChange(event.target.checked),
      });
    }

    // A number field cannot be fully controlled and still be typeable: clamping on
    // every keystroke rewrites "1" into the minimum before the "50" that was coming
    // has been typed, and an empty box reads as zero. The text is therefore local
    // and the value is committed when the field is left or Enter is pressed, which
    // is the only moment the number in it is meant to be finished.
    function Number_(props) {
      const [text, setText] = react.useState(String(props.value));
      const editing = react.useRef(false);
      react.useEffect(() => {
        // A slider drag, a rejected write or a change made elsewhere moves the real
        // value under this box; the text follows unless the reader is typing in it.
        if (!editing.current) setText(String(props.value));
      }, [props.value]);
      const commit = () => {
        const raw = text.trim();
        const next = Number(raw);
        const settled =
          raw === "" || !Number.isFinite(next)
            ? props.value
            : Math.max(props.min, Math.min(props.max, Math.round(next)));
        setText(String(settled));
        if (settled !== props.value) props.onChange(settled);
      };
      return h("input", {
        className: "nc-number",
        type: "number",
        min: props.min,
        max: props.max,
        value: text,
        disabled: props.disabled,
        onFocus: () => {
          editing.current = true;
        },
        onChange: (event) => {
          const raw = event.target.value;
          setText(raw);
          // The card and the number beside it are what the field is for, so a
          // usable number is committed as it is typed. The text stays local, so
          // the partial entry on the way to a longer one ("1" before "16") is
          // never rewritten under the caret - which is what made this field
          // unusable when it clamped every keystroke.
          const next = Number(raw.trim());
          if (raw.trim() === "" || !Number.isFinite(next)) return;
          if (next < props.min || next > props.max) return;
          if (next !== props.value) props.onChange(Math.round(next));
        },
        onBlur: () => {
          editing.current = false;
          commit();
        },
        onKeyDown: (event) => {
          if (event.key === "Enter") {
            event.preventDefault();
            event.currentTarget.blur();
          }
        },
      });
    }

    // A slider is the right control for a setting that is really a dial: it has a
    // sensible middle and it is judged by dragging until the card looks right.
    // There is no step, because a percentage that jumps in fives is a percentage
    // the reader cannot land on; the number beside it is editable, so a dial that
    // needs 63 can be typed instead of nudged.
    //
    // The input carries no step either, and that is not the same thing as no step
    // on the value. A range input with step="1" does not round the number it
    // reports - it moves the thumb in whole steps, so a 140-point range across a
    // 540px track leaves the square parked for three pixels and then jumping four.
    // step="any" is what makes the square sit under the cursor for every pixel of
    // the drag; the value is rounded here, before it reaches anything else, so the
    // number beside it and the card still only ever see whole points.
    function Slider(props) {
      const value = Math.max(props.min, Math.min(props.max, props.value));
      const thumb = react.useRef(null);
      // The thumb belongs to the pointer, not to React. Writing `.value` to a
      // range input while it is held re-anchors the drag in Chromium: the thumb
      // jumps, and every later mouse move is measured from the jumped position,
      // so the square trails the cursor by however far it moved. The input is
      // therefore uncontrolled and written only for a value that did not come
      // from the drag itself - the number typed beside it, or the host reporting
      // a stored value back. `reported` is the drag's own last value, already
      // rounded, so the write-back below never fires mid-drag and the unrounded
      // position the pointer left in the input survives until the drag ends.
      const reported = react.useRef(null);
      react.useEffect(() => {
        const element = thumb.current;
        if (!element) return;
        const next = String(value);
        if (next === reported.current) return;
        reported.current = next;
        element.value = next;
      }, [value]);
      // The drag ends with the input holding the exact pixel the pointer stopped
      // on, which is a fraction of a unit, while the number beside it shows the
      // rounded one. Settling on release is what puts the square where the number
      // says it is - and only on release: writing the input mid-drag is the
      // re-anchoring this whole control is built to avoid.
      react.useEffect(() => {
        const element = thumb.current;
        if (!element) return undefined;
        const settle = () => {
          const next = String(Math.round(Number(element.value)));
          if (element.value === next) return;
          reported.current = next;
          element.value = next;
        };
        element.addEventListener("change", settle);
        return () => element.removeEventListener("change", settle);
      }, []);
      return h(
        "div",
        { className: "nc-slider" },
        h("input", {
          ref: thumb,
          className: "nc-range",
          type: "range",
          min: props.min,
          max: props.max,
          step: "any",
          defaultValue: String(value),
          disabled: props.disabled,
          onChange: (event) => {
            const raw = Number(event.target.value);
            if (!Number.isFinite(raw)) return;
            const next = Math.round(raw);
            reported.current = String(next);
            // While the thumb is moving this is a hold, not a commit: the value goes
            // into the draft (which the preview reads) and only lands in the config
            // once the drag stops. A commit per pixel is what made dragging stutter.
            (props.onDrag || props.onChange)(next);
          },
          // A step-less input moves by a hundredth of its range per arrow key,
          // which is not a step a reader can use on a percentage: 1.4 at a time on
          // a 140-point track. The keyboard keeps its own integer step here, and
          // the thumb is written directly so the next arrow starts from the number
          // that was actually stored rather than from a fractional leftover.
          onKeyDown: (event) => {
            let next;
            if (event.key === "ArrowRight" || event.key === "ArrowUp") next = value + 1;
            else if (event.key === "ArrowLeft" || event.key === "ArrowDown") next = value - 1;
            else if (event.key === "PageUp") next = value + 10;
            else if (event.key === "PageDown") next = value - 10;
            else if (event.key === "Home") next = props.min;
            else if (event.key === "End") next = props.max;
            else return;
            event.preventDefault();
            next = Math.max(props.min, Math.min(props.max, next));
            reported.current = String(next);
            if (thumb.current) thumb.current.value = String(next);
            props.onChange(next);
          },
        }),
        props.editable
          ? h(Number_, {
              value,
              min: props.min,
              max: props.max,
              disabled: props.disabled || props.editDisabled,
              onChange: props.onChange,
            })
          : h("span", { className: "nc-readout" }, props.readout),
        props.auto === undefined
          ? null
          : h(
              "label",
              { className: "nc-inline nc-auto" },
              h("input", {
                type: "checkbox",
                checked: props.auto,
                disabled: props.disabled,
                onChange: (event) => props.onAuto(event.target.checked),
              }),
              props.autoLabel,
            ),
      );
    }

    const CORNERS = [
      ["auto", "跟随任务栏"],
      ["bottom-right", "右下"],
      ["bottom-left", "左下"],
      ["top-right", "右上"],
      ["top-left", "左上"],
    ];
    const THEMES = [
      ["auto", "跟随系统"],
      ["light", "浅色"],
      ["dark", "深色"],
    ];
    const ANIMATIONS = [
      ["follow", "跟随系统"],
      ["on", "开"],
      ["off", "关"],
    ];
    const LANGUAGES = [
      ["auto", "跟随系统"],
      ["zh", "中文"],
      ["en", "English"],
    ];

    function Groups(props) {
      const values = props.values;
      const disabled = props.disabled;
      const set = props.set;
      const drag = props.drag;
      const colors = palette(values);

      const group = (title, rows) =>
        h(
          "section",
          { className: "nc-group" },
          h("h3", { className: "nc-group-title" }, title),
          rows,
        );

      return h(
        "div",
        { className: "nc-page" },
        props.offline
          ? h(
              "p",
              { className: "nc-note" },
              "宿主没有提供配置服务，这里只显示默认值。",
            )
          : null,

        group("弹出什么", [
          h(
            Row,
            { key: "onTurnEnd", label: "任务结束", hint: "本轮任务结束、等你下一步时弹出" },
            h(Toggle, {
              checked: values.onTurnEnd,
              disabled,
              onChange: (next) => set("onTurnEnd", next),
            }),
          ),
          h(
            Row,
            { key: "onApproval", label: "等你审批", hint: "工具请求审批时弹出，随审批一起关闭" },
            h(Toggle, {
              checked: values.onApproval,
              disabled,
              onChange: (next) => set("onApproval", next),
            }),
          ),
          h(
            Row,
            { key: "onQuestion", label: "等你回答", hint: "DSH 向你提问时弹出，随回答一起关闭" },
            h(Toggle, {
              checked: values.onQuestion,
              disabled,
              onChange: (next) => set("onQuestion", next),
            }),
          ),
        ]),

        group("位置与数量", [
          h(
            Row,
            { key: "corner", label: "出现角落", hint: "跟随任务栏：在下就右下，只落在鼠标所在屏幕" },
            h(Select, {
              value: values.corner,
              options: CORNERS,
              disabled,
              onChange: (next) => set("corner", next),
            }),
          ),
          h(
            Row,
            { key: "maxStack", label: "每角最多几张", hint: "屏高放不下时按能放下的张数算，超出的关最早一张" },
            h(Number_, {
              value: values.maxStack,
              min: 1,
              max: 8,
              disabled,
              onChange: (next) => set("maxStack", next),
            }),
          ),
          h(
            Row,
            { key: "seconds", label: "停留秒数", hint: "只影响任务结束卡，范围 3 到 120 秒" },
            h(Number_, {
              value: values.seconds,
              min: 3,
              max: 120,
              disabled,
              onChange: (next) => set("seconds", next),
            }),
          ),
        ]),

        group("外观", [
          h(
            Row,
            { key: "theme", label: "主题", hint: "跟随系统深浅色，也可固定成浅色或深色" },
            h(Select, {
              value: values.theme,
              options: THEMES,
              disabled,
              onChange: (next) => set("theme", next),
            }),
          ),
          h(
            Row,
            { key: "animations", label: "动画", hint: "关掉只去掉入场淡出，位置与停留时间不变" },
            h(Select, {
              value: values.animations,
              options: ANIMATIONS,
              disabled,
              onChange: (next) => set("animations", next),
            }),
          ),
          h(
            Row,
            { key: "scale", label: "缩放", hint: "自动取屏宽 17.5%，夹在 290 到 490 之间；拖动按百分比调整" },
            h(Slider, {
              value: scalePercent(values.scale),
              min: 60,
              max: 200,
              readout:
                values.scale === "" || values.scale === "auto"
                  ? "自动"
                  : undefined,
              editable: !(values.scale === "" || values.scale === "auto"),
              auto: values.scale === "" || values.scale === "auto",
              autoLabel: "自动",
              disabled,
              onAuto: (next) => set("scale", next ? "auto" : String(scalePercent(values.scale))),
              onChange: (next) => set("scale", String(next)),
              onDrag: (next) => drag("scale", String(next)),
            }),
          ),
          h(
            Row,
            { key: "fontSize", label: "字号", hint: "只放大字，卡片宽高与行距位置都不变" },
            h(Slider, {
              value: Math.round(values.fontSize),
              min: 80,
              max: 160,
              editable: true,
              disabled,
              onChange: (next) => set("fontSize", next),
              onDrag: (next) => drag("fontSize", next),
            }),
          ),
          h(
            Row,
            { key: "language", label: "语言", hint: "跟随系统语言，决定卡片文案与字体，时间戳不变" },
            h(Select, {
              value: values.language,
              options: LANGUAGES,
              disabled,
              onChange: (next) => set("language", next),
            }),
          ),
        ]),

        group("颜色", [
          h(
            Row,
            { key: "contrast", label: "对比度", hint: "往右正文更接近标题、边框更重" },
            h(Slider, {
              value: contrastPercent(values.contrast),
              min: 0,
              max: 100,
              editable: true,
              disabled,
              onChange: (next) => set("contrast", String(next)),
              onDrag: (next) => drag("contrast", String(next)),
            }),
          ),
          h(
            "div",
            { className: "nc-preview", key: "preview" },
            [
              ["标题", colors.title],
              ["正文", colors.body],
              ["页眉页脚", colors.muted],
            ].map((entry) =>
              h(
                "div",
                { className: "nc-chip", key: entry[0] },
                h("span", {
                  className: "nc-chip-dot",
                  style: { background: entry[1], borderColor: colors.muted },
                }),
                h(
                  "span",
                  { className: "nc-chip-text", style: { color: entry[1] } },
                  entry[0],
                ),
              ),
            ),
          ),
        ]),
      );
    }

    function Page() {
      const state = react.useSyncExternalStore(subscribe, getSnapshot, () => UNAVAILABLE);

      // A field the user just changed is shown from the draft until the host
      // reports it back, so the control answers the click immediately instead of
      // waiting out the round-trip. A rejected write drops its draft and the
      // real value comes back.
      const values = {};
      for (const field of Object.keys(FIELDS)) {
        const live = liveOf(state, field);
        const held = Object.prototype.hasOwnProperty.call(draft, field) ? draft[field] : undefined;
        values[field] = held !== undefined ? held : live === undefined || live === null ? FIELDS[field] : live;
      }

      // The section is registered as soon as the slots service exists; the
      // config transport may attach a moment later, so a missing form only
      // disables the controls instead of hiding the page.
      const disabled = !form || !state || state.status !== "ready";
      const offline = !form || !state || state.status === "unavailable";

      // The card on the desktop is the settings page's own output: it opens with
      // the page and is redrawn from the values above on every change, so a
      // control is judged against the real card rather than a swatch. Input events
      // arrive faster than the host can rebuild a stack, so schedulePreview keeps
      // one post out at a time and the rest collapse into the newest value.
      const key = JSON.stringify(values);
      react.useEffect(() => {
        schedulePreview(values);
      }, [key]);
      // Letting go of a slider ends the drag, so the held value lands in the config
      // right away instead of waiting out the tail timer. Both events are caught
      // because a drag can end on the track or anywhere in the document.
      react.useEffect(() => {
        window.addEventListener("pointerup", flush);
        window.addEventListener("pointercancel", flush);
        return () => {
          window.removeEventListener("pointerup", flush);
          window.removeEventListener("pointercancel", flush);
        };
      }, []);
      react.useEffect(
        () => () => {
          // A queued post would land after the close and raise the card again.
          closePreview();
        },
        [],
      );

      return h(Groups, { values, set: commit, drag: hold, disabled, offline });
    }

    const CSS = `
.nc-page{--nc-line:rgba(128,128,128,.5);--nc-line-soft:rgba(128,128,128,.28);--nc-fill:rgba(128,128,128,.1);display:flex;flex-direction:column;gap:0;color:var(--dsw-alias-label-primary,inherit);font-family:var(--dsw-font-family,inherit);font-size:13px}
.nc-group{display:flex;flex-direction:column;gap:10px;padding:16px 0;border-top:1px solid var(--nc-line-soft)}
.nc-group:first-of-type{border-top:none;padding-top:0}
.nc-group-title{margin:0 0 2px;font-size:13px;font-weight:600;color:var(--dsw-alias-label-primary,inherit)}
.nc-row{display:grid;grid-template-columns:112px minmax(0,1fr);grid-template-rows:auto auto;column-gap:12px;row-gap:2px;align-items:center;min-height:28px;padding:2px 0}
.nc-label{grid-column:1;grid-row:1;color:var(--dsw-alias-label-secondary,inherit)}
.nc-control{grid-column:2;grid-row:1;display:flex;align-items:center;gap:8px;min-width:0;flex-wrap:nowrap}
.nc-hint{grid-column:2;grid-row:2;color:var(--dsw-alias-label-tertiary,inherit);font-size:12px;line-height:1.4}
/* The form chrome is drawn with the greys above, not with the host border
   tokens: --dsw-alias-border-l2 is a 10% ink, and at 125% DPI a 1px border
   spreads that 10% across three device rows at ~3% each - the field is there,
   it just cannot be seen. The fill is the same story, because the layer token
   is the page background in light mode. These greys sit mid-way so they read
   on a white page and on a dark one. */
.nc-select,.nc-number{height:28px;padding:0 8px;border-radius:var(--dsw-radius-sm,6px);border:1px solid var(--nc-line);background:var(--nc-fill);color:inherit;font:inherit;min-width:96px}
/* A native select on Windows draws its own chrome. appearance:none removes it
   and the chevron below replaces the arrow, so the box is ours on every
   platform and after every choice. */
.nc-select{appearance:none;-webkit-appearance:none;-moz-appearance:none;padding-right:26px;background-image:url("data:image/svg+xml;charset=utf-8,%3Csvg xmlns='http://www.w3.org/2000/svg' width='10' height='6' viewBox='0 0 10 6'%3E%3Cpath d='M1 1L5 5L9 1' fill='none' stroke='%23888890' stroke-width='1.5' stroke-linecap='round' stroke-linejoin='round'/%3E%3C/svg%3E");background-repeat:no-repeat;background-position:right 8px center;background-color:var(--nc-fill);cursor:pointer}
.nc-select:disabled{cursor:default;opacity:.6}
.nc-select option{background:var(--dsw-alias-bg-base,#fff);color:var(--dsw-alias-label-primary,#000)}
.nc-number{width:88px;min-width:88px}
/* A slider carries three things at once - the track, the number, and for scale
   the option to hand control back to the screen - so they sit in one row and the
   readout has a fixed slot to keep the tracks lined up with each other. */
.nc-slider{display:flex;align-items:center;gap:10px;min-width:0;flex:1 1 auto}
.nc-range{appearance:none;-webkit-appearance:none;flex:1 1 auto;min-width:96px;width:auto;height:18px;background:transparent;cursor:pointer;margin:0}
.nc-range:disabled{cursor:default;opacity:.5}
.nc-range::-webkit-slider-runnable-track{height:4px;border-radius:2px;background:var(--nc-fill);border:1px solid var(--nc-line-soft)}
.nc-range::-webkit-slider-thumb{appearance:none;-webkit-appearance:none;width:14px;height:14px;margin-top:-6px;border-radius:50%;background:var(--dsw-alias-brand-primary,#4d6bfe);border:1px solid var(--nc-line)}
.nc-range::-moz-range-track{height:4px;border-radius:2px;background:var(--nc-fill)}
.nc-range::-moz-range-thumb{width:12px;height:12px;border-radius:50%;background:var(--dsw-alias-brand-primary,#4d6bfe);border:1px solid var(--nc-line)}
.nc-range:focus-visible{outline:2px solid var(--dsw-focus-ring-color,#4d6bfe);outline-offset:2px;border-radius:3px}
/* The readout and the number field have to occupy the same width: the scale row
   swaps between them the moment a drag starts (dragging an "auto" slider is how
   you take it off auto), and a different width there reflows the row under the
   pointer mid-drag - the track shrinks, so the thumb jumps away from the cursor
   that is holding it. Both boxes are pinned here rather than left to the host's
   box-sizing, which is the one thing that would make them differ again. */
.nc-slider .nc-number,.nc-readout{box-sizing:border-box;width:88px;min-width:88px}
.nc-readout{padding:0 8px;font-variant-numeric:tabular-nums;color:var(--dsw-alias-label-secondary,inherit);font-size:12px}
.nc-auto{margin-left:2px}
.nc-check{width:16px;height:16px;accent-color:var(--dsw-alias-brand-primary,#4d6bfe);cursor:pointer}
.nc-inline{display:flex;align-items:center;gap:6px;color:var(--dsw-alias-label-secondary,inherit);font-size:12px;cursor:pointer}
.nc-preview{display:flex;gap:16px;padding:10px 12px;border-radius:var(--dsw-radius-md,8px);background:var(--nc-fill);grid-column:1/-1}
.nc-chip{display:flex;align-items:center;gap:6px;font-size:12px}
.nc-chip-dot{width:12px;height:12px;border-radius:50%;border:1px solid transparent}
.nc-note{margin:0 0 8px;color:var(--dsw-alias-label-tertiary,inherit);font-size:12px}
`;

    function injectCss() {
      const id = "dsh-notice-card/settings.css";
      try {
        if (typeof document === "undefined") return;
        if (document.querySelector("style[data-plugin-css=" + JSON.stringify(id) + "]")) return;
        const tag = document.createElement("style");
        tag.dataset.plugin = "dsh-notice-card";
        tag.dataset.pluginCss = id;
        tag.textContent = CSS;
        document.head.appendChild(tag);
      } catch {}
    }

    function apply(ctx) {
      injectCss();

      ctx.inject(["configForms"], (child) => {
        attach(child.configForms?.get(NS) ?? null);
        return () => attach(null);
      });

      ctx.effect(
        () =>
          ctx.slots.inject("settings.section", () =>
            ctx.slots.register(
              {
                name: "settings.section",
                id: NS,
                order: 32,
                label: () => "通知卡",
              },
              Page,
            ),
          ),
        "dsh-notice-card: settings page",
      );
    }

    exports.apply = apply;
    exports.inject = inject;
    return module.exports;
  },
});
