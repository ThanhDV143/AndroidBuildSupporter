using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace ThanhDV.AndroidBuildSupporter
{
    /// <summary>Where a field's value lives; shown as a coloured dot before its label.</summary>
    internal enum Source
    {
        /// <summary>Read from and written to Unity's own settings.</summary>
        Unity,
        /// <summary>This tool's settings in ProjectSettings/, committed.</summary>
        Tool,
        /// <summary>This machine only, in UserSettings/.</summary>
        Local
    }

    /// <summary>
    /// Builds the window on one grid: every row is a fixed-width label column and a value column, so all controls,
    /// buttons and hints line up. Values are read through getters and written through setters; <see cref="Refresh"/>
    /// reads them all again so changes made elsewhere show up, skipping a field while it is being edited.
    /// </summary>
    internal sealed class Form
    {
        private readonly List<Action> refreshers = new List<Action>();

        public void OnRefresh(Action refresh) => refreshers.Add(refresh);

        public void Refresh()
        {
            foreach (Action refresh in refreshers) refresh();
        }

        // --- Structure -----------------------------------------------------------------------------------------

        public static VisualElement Card(VisualElement parent, string title, string description = null)
        {
            var card = new VisualElement();
            card.AddToClassList("abs-card");
            var header = new Label(title);
            header.AddToClassList("abs-card-title");
            card.Add(header);
            if (description != null)
            {
                var text = new Label(description);
                text.AddToClassList("abs-card-description");
                card.Add(text);
            }
            parent.Add(card);
            return card;
        }

        /// <summary>A labelled row; returns its value column, where controls and buttons go side by side.</summary>
        public static VisualElement Row(VisualElement parent, string label, Source? source = null, string tooltip = null)
        {
            var row = new VisualElement();
            row.AddToClassList("abs-row");
            row.Add(LabelColumn(label, source, tooltip));
            var value = new VisualElement();
            value.AddToClassList("abs-value");
            row.Add(value);
            parent.Add(row);
            return value;
        }

        public static VisualElement LabelColumn(string text, Source? source, string tooltip = null)
        {
            var label = new VisualElement();
            label.AddToClassList("abs-label");
            var dot = new VisualElement();
            dot.AddToClassList("abs-dot");
            if (source != null) dot.AddToClassList("abs-dot-" + source.Value.ToString().ToLowerInvariant());
            label.Add(dot);
            label.Add(new Label(text) { name = "abs-label-text" });
            label.tooltip = tooltip ?? (source == null ? null : Describe(source.Value));
            return label;
        }

        public static string Describe(Source source) =>
            source == Source.Unity ? "Synced with Unity's own settings" :
            source == Source.Tool ? "Saved in ProjectSettings/ (committed)" : "Saved in UserSettings/ on this machine (not committed)";

        public static Button Button(VisualElement value, string text, Action click, string tooltip = null)
        {
            var button = new Button(click) { text = text, tooltip = tooltip };
            button.AddToClassList("abs-button");
            value.Add(button);
            return button;
        }

        /// <summary>A muted line under a row, aligned with the value column.</summary>
        public Label Hint(VisualElement parent, Func<string> get)
        {
            var hint = new Label();
            hint.AddToClassList("abs-hint");
            OnRefresh(() =>
            {
                string text = get();
                hint.text = text ?? "";
                hint.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
            });
            parent.Add(hint);
            return hint;
        }

        public Label Hint(VisualElement parent, string text) => Hint(parent, () => text);

        /// <summary>How long a notice stays before it hides itself.</summary>
        public const long NoticeMs = 3000;

        /// <summary>
        /// A tinted box across the card, shown while <paramref name="get"/> returns text. A problem that blocks the
        /// build stays until it is fixed; the result of something the user just did (<paramref name="autoHide"/>)
        /// hides <see cref="NoticeMs"/> after it appears, and shows again when the text changes. Text ending in "…"
        /// says work is in progress and stays until it is replaced.
        /// </summary>
        /// <param name="kind">info, ok, warning or error; read again on every refresh.</param>
        public Label Notice(VisualElement parent, Func<string> get, Func<string> kind, bool autoHide = false)
        {
            var notice = new Label();
            notice.AddToClassList("abs-notice");
            string shown = null;
            string current = null;
            bool expired = false;
            IVisualElementScheduledItem hide = null;
            OnRefresh(() =>
            {
                string text = get();
                if (text != current)
                {
                    current = text;
                    expired = false;
                    hide?.Pause();
                    hide = null;
                    if (autoHide && !string.IsNullOrEmpty(text) && !text.EndsWith("…", StringComparison.Ordinal))
                        hide = notice.schedule.Execute(() =>
                        {
                            expired = true;
                            notice.style.display = DisplayStyle.None;
                        }).StartingIn(NoticeMs);
                }
                notice.text = text ?? "";
                notice.style.display = string.IsNullOrEmpty(text) || expired ? DisplayStyle.None : DisplayStyle.Flex;
                string now = kind();
                if (now == shown) return;
                if (shown != null) notice.RemoveFromClassList("abs-notice-" + shown);
                notice.AddToClassList("abs-notice-" + now);
                shown = now;
            });
            parent.Add(notice);
            return notice;
        }

        public Label Notice(VisualElement parent, Func<string> get, string kind, bool autoHide = false) => Notice(parent, get, () => kind, autoHide);

        // --- Fields --------------------------------------------------------------------------------------------

        public TextField Text(VisualElement parent, string label, Func<string> get, Action<string> set, Source source, bool password = false)
        {
            VisualElement value = Row(parent, label, source);
            return TextIn(value, get, set, password);
        }

        /// <summary>A text field inside an existing value column, next to other controls.</summary>
        public TextField TextIn(VisualElement value, Func<string> get, Action<string> set, bool password = false)
        {
            var field = new TextField { isDelayed = true, isPasswordField = password };
            field.AddToClassList("abs-control");
            field.RegisterValueChangedCallback(e =>
            {
                set(e.newValue ?? "");
                Refresh();
            });
            if (get != null)
                OnRefresh(() =>
                {
                    if (!IsEditing(field)) field.SetValueWithoutNotify(get() ?? "");
                });
            value.Add(field);
            return field;
        }

        public Toggle Toggle(VisualElement parent, string label, Func<bool> get, Action<bool> set, Source source)
        {
            VisualElement value = Row(parent, label, source);
            return Check(value, null, get, set);
        }

        /// <summary>A checkbox with its text after the box, inside a value column.</summary>
        public Toggle Check(VisualElement value, string text, Func<bool> get, Action<bool> set)
        {
            var field = new Toggle { text = text };
            field.AddToClassList("abs-check");
            field.RegisterValueChangedCallback(e =>
            {
                set(e.newValue);
                Refresh();
            });
            OnRefresh(() => field.SetValueWithoutNotify(get()));
            value.Add(field);
            return field;
        }

        /// <summary>A row whose value column holds checkboxes in two equal columns; fill it with <see cref="Cell"/> and <see cref="Gap"/>.</summary>
        public static VisualElement Grid(VisualElement parent, string label, Source source)
        {
            VisualElement value = Row(parent, label, source);
            value.AddToClassList("abs-checks");
            return value;
        }

        public Toggle Cell(VisualElement grid, string text, Func<bool> get, Action<bool> set)
        {
            Toggle toggle = Check(grid, text, get, set);
            toggle.AddToClassList("abs-check-cell");
            return toggle;
        }

        /// <summary>An empty cell, so the next checkbox starts the other column.</summary>
        public static void Gap(VisualElement grid)
        {
            var gap = new VisualElement();
            gap.AddToClassList("abs-check-cell");
            grid.Add(gap);
        }

        /// <summary>A grid of checkboxes filled left to right, two per line.</summary>
        public VisualElement Checks(VisualElement parent, string label, Source source, params (string text, Func<bool> get, Action<bool> set)[] items)
        {
            VisualElement grid = Grid(parent, label, source);
            foreach (var item in items) Cell(grid, item.text, item.get, item.set);
            return grid;
        }

        /// <summary>A drop-down whose choices can change; the current value is always one of them.</summary>
        public PopupField<T> Popup<T>(VisualElement parent, string label, Func<List<T>> choices, Func<T> get, Action<T> set,
            Func<T, string> format, Source source)
        {
            VisualElement value = Row(parent, label, source);
            var field = new PopupField<T>(WithValue(choices(), get()), get(), format, format);
            field.AddToClassList("abs-control");
            field.RegisterValueChangedCallback(e =>
            {
                set(e.newValue);
                Refresh();
            });
            OnRefresh(() =>
            {
                T current = get();
                List<T> now = WithValue(choices(), current);
                if (!now.SequenceEqual(field.choices)) field.choices = now;
                field.SetValueWithoutNotify(current);
            });
            value.Add(field);
            return field;
        }

        /// <summary>A read-only value.</summary>
        public Label Info(VisualElement parent, string label, Func<string> get, Source source)
        {
            VisualElement value = Row(parent, label, source);
            var text = new Label();
            text.AddToClassList("abs-info");
            OnRefresh(() => text.text = get());
            value.Add(text);
            return text;
        }

        /// <summary>Strings shown as chips with a remove button, and a field to add one.</summary>
        public void StringList(VisualElement parent, string label, Func<List<string>> get, Action<List<string>> set,
            Func<string, string> validate, Func<string, bool> highlight, Source source, string placeholder)
        {
            VisualElement value = Row(parent, label, source);
            value.AddToClassList("abs-list");
            var chips = new VisualElement();
            chips.AddToClassList("abs-chips");
            var add = new VisualElement();
            add.AddToClassList("abs-inline");
            var input = new TextField { tooltip = placeholder };
            input.AddToClassList("abs-control");
            var error = new Label();
            error.AddToClassList("abs-inline-error");
            error.style.display = DisplayStyle.None;

            void Add()
            {
                string item = (input.value ?? "").Trim();
                if (item.Length == 0) return;
                string problem = validate?.Invoke(item);
                error.text = problem ?? "";
                error.style.display = problem == null ? DisplayStyle.None : DisplayStyle.Flex;
                if (problem != null) return;
                List<string> items = get();
                if (!items.Contains(item)) set(items.Concat(new[] { item }).ToList());
                input.SetValueWithoutNotify("");
                Refresh();
            }

            input.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
                Add();
                e.StopPropagation();
            });
            input.RegisterValueChangedCallback(_ => error.style.display = DisplayStyle.None);
            add.Add(input);
            Button(add, "Add", Add, "Add " + placeholder);
            value.Add(chips);
            value.Add(add);
            value.Add(error);

            string shown = null;
            OnRefresh(() =>
            {
                List<string> items = get();
                string key = string.Join("\n", items.Select(i => i + (highlight?.Invoke(i) ?? false)));
                if (key == shown) return;
                shown = key;
                chips.Clear();
                foreach (string item in items)
                {
                    var chip = new VisualElement();
                    chip.AddToClassList("abs-chip");
                    if (highlight?.Invoke(item) ?? false) chip.AddToClassList("abs-chip-highlight");
                    chip.Add(new Label(item));
                    string captured = item;
                    var remove = new Button(() =>
                    {
                        set(get().Where(i => i != captured).ToList());
                        Refresh();
                    }) { text = "×", tooltip = "Remove " + item };
                    remove.AddToClassList("abs-chip-remove");
                    chip.Add(remove);
                    chips.Add(chip);
                }
                chips.style.display = items.Count == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            });
        }

        private static List<T> WithValue<T>(List<T> choices, T value)
        {
            var list = new List<T>(choices);
            if (!list.Contains(value)) list.Insert(0, value);
            return list;
        }

        private static bool IsEditing(VisualElement field) =>
            field.panel?.focusController?.focusedElement is VisualElement focused && (focused == field || field.Contains(focused));
    }
}
