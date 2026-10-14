---
title: Share and embed a read-only page
description: Give people without an account a read-only page of a project or a view, protect it with a password and an expiry, revoke it, and embed a view in another site.
---

This guide shows you how to share a project or a view with a person who has no account, and how to show a view in a page of another site, such as a wiki. A share link is read-only. The person who opens it cannot change anything.

## Know what a link shows

A link shows:

- the diagrams, with pan, zoom, drill-in and the details of each element and relationship;
- the truth label of each fact. A citation shows as `path:lines` text only;
- the contracts: operations, channels, messages and payload schemas;
- for a project link: the flows, the docs of main and the accepted decisions (ADRs).

Two parts are off by default. You turn each one on for each link:

- **Show estimated costs**: the estimate of main, line by line, with the lines that have no known price;
- **Show promises with their verification status**: the approved promises and the result of each obligation.

A link never shows:

- a draft, an open import proposal or an inferred fact that no person accepted. An element that stands on a hidden fact is hidden too;
- source text. A citation names the file and the lines, never the repository, the commit or the code;
- comments, members, settings, AI conversations and scan details.

## Create a link

You need the Editor or the Owner role.

1. Open the project.
2. Select **Share** in the top bar.
3. Select the **Scope**: **The whole project**, or **One view** and the view. Only a view link can be embedded.
4. Select the **Mode**:
   - **Live** follows the accepted model on main. A visitor sees a change after a reload.
   - **Pinned** shows one named revision. Select the **Revision**. The page labels the revision and its date, and it does not change after you create the link. The flows, docs, decisions, costs and promises of a pinned page are those of the moment you create the link.
5. Optional: type a **Password**. The workspace link policy can make the password required.
6. Optional: set **Expires**. After that moment the link answers "not found".
7. Optional: type a **Name for the members**. Visitors do not see it.
8. Turn on **Show estimated costs** or **Show promises** if the visitor needs them.
9. Select **Create link**.
10. Copy the **Link**. For a view link, copy the **Embed code** too.

Vellum keeps only a hash of the link. Copy it now: Vellum cannot show it again. If you lose it, create a new link and revoke the old one.

The request is `POST /api/projects/{projectId}/share-links`.

## Open a link

The visitor opens the link in a browser. No account and no sign-in is necessary. A password link first asks for the password. After the right password, the page opens for 12 hours in that browser tab.

After 10 wrong passwords in 15 minutes, the link refuses every password until the 15 minutes end. A password from one address is also limited to 10 attempts in each minute.

## Change or revoke a link

The person who created a link can change and revoke it. An Owner can change and revoke every link.

1. Select **Share**.
2. Find the link in **Links**. Each link shows its status, its mode, its protection, who created it, its expiry and the time it was last opened. Vellum records no information about the visitors.
3. Use the controls of the link:
   - **Costs** and **Promises** turn the two parts on or off;
   - **Set password**, **Change password** or **Remove password**. A new password ends every open session of the old one;
   - **Remove expiry**;
   - **Revoke**. A revoked link answers "not found" for everybody, and you cannot undo it.

The audit log of the workspace records each creation, change and revocation of a link. It never records the link or the password.

## Set the link policy

You need the Owner role.

1. Open the workspace settings.
2. Select **Sharing**.
3. Set **Link policy**:
   - **Off**: no link works;
   - **Password only**: a link without a password answers "not found";
   - **Public allowed**: a link can have a password or none.
4. Select **Save changes**.

The policy applies to the links that exist. A public link under **Password only** answers "not found". It does not ask for a password, because it has none. If you allow public links again, the link works again. In **Share**, such a link has the status `blocked`.

A hosted instance starts with **Password only**. A local instance starts with **Public allowed**.

## Embed a view in another site

1. Create a link with the scope **One view**.
2. Open the workspace settings and select **Sharing**.
3. In **Embed allow-list**, type the origin of the site, for example `https://wiki.example.com` or `https://*.atlassian.net`. Separate two origins with a comma.
4. Select **Save changes**.
5. Put the **Embed code** in the page of that site:

```html
<iframe src="https://vellum.example.com/embed/<token>" width="100%" height="600" style="border:0" title="Architecture view"></iframe>
```

The embedded page shows the diagram, pan, zoom, drill-in and the details of each element. It has no app shell. A password link asks for the password inside the frame.

Only the page `/embed/{token}` of a view link can be shown in a frame, and only by the origins in the list. The list starts empty, so no site can frame a view until an Owner adds its origin. Every other page of Vellum refuses every frame.

## Related

- [Settings reference: `sharing`](/reference/settings/#sharing)
- [Security model](/concepts/security-model/)
