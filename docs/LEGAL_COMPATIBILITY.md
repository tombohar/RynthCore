# Legal Compatibility Guardrails

This project is building a new RynthCore-native plugin system first, with clean-room compatibility shims only where they are safe and useful.

This file is a project policy note, not legal advice.

## Operating Position

- We may build compatibility with publicly documented or behaviorally observed plugin interfaces.
- We may use the AC client decompile and runtime behavior to locate safe host-side hook points in `acclient.exe`.
- We do not decompile, disassemble, patch, or copy code from Decal, VTank, or any other closed plugin.
- We do not redistribute Decal, VTank, or third-party closed plugin binaries.
- If users want to test third-party compatibility, they provide their own binaries.

## Why This Direction Is Safer

- The historical Decal project listing publishes Decal under a `Public Domain` license.
- Decal still exposes public developer-facing materials such as plugin templates.
- VTank publishes a license agreement that forbids reverse engineering, decompiling, disassembling, and redistribution.
- U.S. law includes an interoperability exception in `17 U.S.C. 1201(f)`, but RynthCore should treat that as a narrow backstop instead of the core project strategy.

## Project Rules

- Do not reverse engineer Decal binaries.
- Do not reverse engineer VTank binaries.
- Do not copy source, headers, resources, strings, or assets from closed plugins.
- Do not market RynthCore as an official Decal replacement or imply endorsement.
- Prefer source/API compatibility over binary cloning.
- Prefer RynthCore-native plugins over legacy binary compatibility whenever possible.
- If a compatibility task would require inspecting closed-source plugin internals, stop and reassess before proceeding.

## Allowed Sources For Compatibility Work

- Public Decal-facing templates and documentation.
- The AC client decompile and the running AC client itself.
- RynthCore code and user-provided test plugins.
- Public legal/license text published by the relevant project owners.

## Current Safe First Step

The safest near-term compatibility work is implementing Decal-shaped lifecycle and callback seams inside RynthCore, starting with events such as `OnLoginComplete`, chat events, target changes, and trade events, without inspecting or copying Decal or VTank implementation details.

## References

- Decal project page: <https://sourceforge.net/projects/decaldev/>
- Decal templates: <https://www.decaldev.com/templates>
- Virindi Tank license agreement: <https://virindi.net/wiki/index.php/Virindi_Tank_license_agreement>
- 17 U.S.C. 1201: <https://uscode.house.gov/view.xhtml?req=%28title:17%20section:1201%20edition:prelim%29>
