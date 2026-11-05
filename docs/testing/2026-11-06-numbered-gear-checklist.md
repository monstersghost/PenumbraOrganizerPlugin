# Manual check: numbered gear folders and padded hair

Load the build, scan a real library, then check each item.

- [ ] Both gear splits on, Group by Mod type: Sort. Review Changes shows `Gear/00. Sets`, `01.`-`09.`
      slot folders and `10.`+ body folders; nothing loose directly in `Gear/`.
- [ ] Your first-choice body (Rue+) is `10.`; a body with no gear has no folder and no gap.
- [ ] Inside a body folder the slot numbers match the top level (`02. Top`).
- [ ] Reorder Body priority, Sort again: body numbers follow the new order.
- [ ] Only the slot split, or only the body split: layout is as before, no numbers.
- [ ] Hair split on: `Hair/002`, `Hair/053`, `Hair/185` list in numeric order in Penumbra.
- [ ] Apply works (no `PathRenameFailed`); Penumbra shows `01. Head`-style names exactly as written.
- [ ] A template with both gear splits ticked previews and applies with numbered gear; a template
      with `Gear` renamed to `Equipment` renames the root.
- [ ] History: Restore puts the old layout back.
- [ ] Verify: no vanilla hair number is 1000 or more.
