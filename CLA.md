# Bevel Desktop — Contributor License Agreement

**Why this exists.** Bevel is developed open-core: this repository is Apache-2.0, and a separate
private repository holds proprietary components that official builds combine with it. For that to
be lawful, one party has to hold the rights to relicense the open code into those builds.

Without this agreement, merging your patch would mean the project no longer has clear authority
to ship a proprietary build containing it — and that cannot be undone afterwards except by
tracking down every contributor and asking. So this is asked **once, up front**, rather than
discovered later.

It is not a copyright assignment. **You keep the copyright in your contribution.** You are
granting a licence alongside it.

## Agreement

By submitting a contribution to this project, you agree to the following.

### 1. Definitions

"Contribution" means any work of authorship you intentionally submit for inclusion in the
project — code, documentation, tests, assets, configuration — through a pull request, patch,
issue attachment, or any other means.

"You" means the copyright owner, or the entity authorised by the copyright owner, entering into
this agreement.

### 2. Copyright licence

You retain all right, title and interest in your Contribution. You grant the project maintainer
(Cezar "ikari" Pokorski) a perpetual, worldwide, non-exclusive, royalty-free, irrevocable
copyright licence to reproduce, prepare derivative works of, publicly display, publicly perform,
sublicense and distribute your Contribution and such derivative works, **under any licence terms,
including proprietary terms**.

This is what permits your Contribution to appear both in the Apache-2.0 repository and in a
proprietary official build.

### 3. Patent licence

You grant a perpetual, worldwide, non-exclusive, royalty-free, irrevocable patent licence to
make, have made, use, offer to sell, sell, import and otherwise transfer your Contribution,
limited to those patent claims licensable by you that are necessarily infringed by your
Contribution alone or by its combination with the project.

If any entity institutes patent litigation alleging that the project or a Contribution
constitutes patent infringement, the patent licences granted under this agreement to that entity
terminate.

### 4. Your representations

You confirm that:

- Each Contribution is your original creation, or you have the right to submit it under this
  agreement.
- You are legally entitled to grant the above licences. **If your employer has rights to
  intellectual property you create**, you have either received permission to make the
  Contribution on their behalf, or your employer has waived such rights, or your employer has
  authorised you to submit on their behalf.
- Your Contribution does not knowingly include code that would subject the project to licence
  terms incompatible with the above — in particular **no GPL, LGPL, or other copyleft-licensed
  code**, and no code you cannot identify the provenance of.
- If any part of your Contribution is **not** your original creation, you have identified its
  source, its licence, and any restrictions, in the submission itself.

### 5. No obligation, no warranty

Nothing here obliges the project to use or include your Contribution. You provide your
Contribution "AS IS", without warranties or conditions of any kind, except as stated in
section 4.

---

## How to agree

Add a `Signed-off-by` line to each commit, which you can do automatically with `git commit -s`:

```
Signed-off-by: Your Name <your.email@example.com>
```

Signing off certifies that you have read this document and agree to it for that contribution, and
that the name and email are your own real identity.

For a substantial contribution, or if you are contributing on behalf of an employer, say so in the
pull request so the ownership question is settled in the open rather than assumed.

## Third-party code

Do not paste code from Stack Overflow, another project, or an AI assistant's output without
establishing its provenance and licence first. If in doubt, open an issue and ask before writing
the patch — it is far cheaper than removing it afterwards.
