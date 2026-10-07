"""
Generate the NorthBank Payments sample architecture in every diagram format SecureFlow reads.

One definition (below) is written out as draw.io, Mermaid, C4-PlantUML, Structurizr DSL, Visio .vsdx,
Excalidraw, Gliffy and a plain SVG export, so every file describes exactly the same system and the demo can show
"same system, many tools, same analysis". The files are real files in each tool's format, generated here rather
than exported from the tools themselves (see samples/diagrams/README.md).

    python scripts/make-sample-diagrams.py [output-dir]      (default: samples/diagrams)

PNG and PDF renders of the SVG are made separately with a headless browser (scripts/render-sample-diagrams.ps1).
"""
import html
import json
import math
import os
import sys
import uuid
import zipfile

TITLE = "NorthBank Payments"

# id, label, technology, notes (annotations the rules read), kind, zone, column, row
NODES = [
    ("customer", "Customer", "Mobile & Web banking", "", "user", "internet", 0, 1),
    ("cardnet", "Card Network", "Visa / Mastercard", "", "external", "internet", 0, 3),
    ("gateway", "API Gateway", "Azure APIM", "x2", "gateway", "dmz", 1, 1),
    ("api", "Payments API", ".NET", "x2", "api", "private", 2, 1),
    ("fraud", "Fraud Service", "Python", "x1", "api", "private", 3, 0),
    ("queue", "Settlement Queue", "Service Bus", "", "queue", "private", 3, 2),
    ("worker", "Settlement Worker", "", "x1", "worker", "private", 3, 3),
    ("db", "Payments DB", "SQL Server", "x1", "database", "private", 2, 2),
    ("vault", "Key Vault", "", "", "secrets", "private", 4, 0),
    ("monitor", "Monitoring", "App Insights", "", "monitoring", "private", 4, 2),
]
# from, to, label, async (dashed)
EDGES = [
    ("customer", "gateway", "HTTPS OAuth", False),
    ("gateway", "api", "HTTPS JWT", False),
    ("api", "fraud", "HTTP no auth", False),
    ("api", "db", "SQL", False),
    ("api", "queue", "AMQPS publish", True),
    ("queue", "worker", "AMQPS subscribe", True),
    ("worker", "cardnet", "HTTPS mTLS", False),
    ("worker", "db", "SQL", False),
    ("api", "vault", "HTTPS managed identity", False),
    ("api", "monitor", "async telemetry", True),
]
ZONES = [("internet", "Internet"), ("dmz", "DMZ"), ("private", "Private Network")]

W, H = 170, 70                      # shape size (px)
COL = [40, 270, 500, 730, 960]      # column x
ROW = [70, 210, 350, 490]           # row y
PAD = 28


def label(n, sep=" "):
    """Label as most tools show it: name, technology in brackets, then replica annotation."""
    _, name, tech, notes, *_ = n
    s = name
    if tech:
        s += f"{sep}({tech})"
    if notes:
        s += f"{sep}{notes}"
    return s


def pos(n):
    return COL[n[6]], ROW[n[7]]


def zone_box(zone):
    members = [n for n in NODES if n[5] == zone]
    xs = [pos(n)[0] for n in members]
    ys = [pos(n)[1] for n in members]
    return min(xs) - PAD, min(ys) - PAD - 18, max(xs) + W + PAD - (min(xs) - PAD), max(ys) + H + PAD - (min(ys) - PAD - 18)


def node(nid):
    return next(n for n in NODES if n[0] == nid)


def endpoints(a, b):
    """Where an arrow from a to b leaves and enters: facing sides, or top/bottom when stacked in one column."""
    ax, ay = pos(node(a)); bx, by = pos(node(b))
    if bx == ax:
        return (ax + W / 2, ay + H, bx + W / 2, by) if by > ay else (ax + W / 2, ay, bx + W / 2, by + H)
    if bx > ax:
        return ax + W, ay + H / 2, bx, by + H / 2
    return ax, ay + H / 2, bx + W, by + H / 2


# ---------------------------------------------------------------- draw.io
def drawio():
    style = {
        "user": "shape=umlActor;verticalLabelPosition=bottom;verticalAlign=top;html=1;",
        "external": "shape=cloud;whiteSpace=wrap;html=1;fillColor=#f5f5f5;",
        "gateway": "rounded=1;whiteSpace=wrap;html=1;fillColor=#dae8fc;",
        "api": "rounded=1;whiteSpace=wrap;html=1;fillColor=#d5e8d4;",
        "queue": "shape=mxgraph.flowchart.stored_data;whiteSpace=wrap;html=1;fillColor=#fff2cc;",
        "worker": "rounded=1;whiteSpace=wrap;html=1;fillColor=#e1d5e7;",
        "database": "shape=cylinder3;whiteSpace=wrap;html=1;boundedLbl=1;size=12;fillColor=#f8cecc;",
        "secrets": "rounded=1;whiteSpace=wrap;html=1;fillColor=#ffe6cc;",
        "monitoring": "rounded=1;whiteSpace=wrap;html=1;fillColor=#f5f5f5;",
    }
    cells = ['<mxCell id="0" />', '<mxCell id="1" parent="0" />']
    for zid, zname in ZONES:
        x, y, w, h = zone_box(zid)
        cells.append(f'<mxCell id="zone-{zid}" value="{html.escape(zname)}" style="swimlane;dashed=1;html=1;startSize=24;fillColor=none;" vertex="1" parent="1">'
                     f'<mxGeometry x="{x}" y="{y}" width="{w}" height="{h}" as="geometry" /></mxCell>')
    for n in NODES:
        zx, zy, _, _ = zone_box(n[5])
        x, y = pos(n)
        cells.append(f'<mxCell id="{n[0]}" value="{html.escape(label(n))}" style="{style[n[4]]}" vertex="1" parent="zone-{n[5]}">'
                     f'<mxGeometry x="{x - zx}" y="{y - zy}" width="{W}" height="{H}" as="geometry" /></mxCell>')
    for i, (a, b, text, is_async) in enumerate(EDGES, 1):
        dash = "dashed=1;" if is_async else ""
        cells.append(f'<mxCell id="e{i}" value="{html.escape(text)}" style="edgeStyle=orthogonalEdgeStyle;rounded=0;html=1;{dash}endArrow=block;" edge="1" parent="1" source="{a}" target="{b}">'
                     '<mxGeometry relative="1" as="geometry" /></mxCell>')
    return ('<mxfile host="app.diagrams.net" type="device">\n'
            f'  <diagram id="northbank" name="{TITLE}">\n'
            '    <mxGraphModel dx="1400" dy="800" grid="1" gridSize="10" guides="1" tooltips="1" connect="1" arrows="1" fold="1" page="1" pageScale="1" pageWidth="1200" pageHeight="700">\n'
            '      <root>\n        ' + "\n        ".join(cells) + '\n      </root>\n    </mxGraphModel>\n  </diagram>\n</mxfile>\n')


# ---------------------------------------------------------------- Mermaid
def mermaid():
    shape = {"user": ('(["', '"])'), "database": ('[("', '")]'), "queue": ('[/"', '"/]'), "external": ('{{"', '"}}')}
    out = ["---", f"title: {TITLE}", "---", "flowchart LR"]
    for zid, zname in ZONES:
        out.append(f'  subgraph {zid}["{zname}"]')
        for n in [n for n in NODES if n[5] == zid]:
            o, c = shape.get(n[4], ('["', '"]'))
            out.append(f"    {n[0]}{o}{label(n)}{c}")
        out.append("  end")
    out.append("")
    for a, b, text, is_async in EDGES:
        arrow = "-.->" if is_async else "-->"
        out.append(f"  {a} {arrow}|{text}| {b}")
    return "\n".join(out) + "\n"


# ---------------------------------------------------------------- C4-PlantUML
def plantuml():
    macro = {"user": "Person", "external": "System_Ext", "database": "ContainerDb", "queue": "ContainerQueue"}
    out = ["@startuml",
           "!include https://raw.githubusercontent.com/plantuml-stdlib/C4-PlantUML/master/C4_Container.puml",
           "LAYOUT_LEFT_RIGHT()", f"title {TITLE}", ""]
    for zid, zname in ZONES:
        out.append(f'Boundary({zid}, "{zname}") {{')
        for n in [n for n in NODES if n[5] == zid]:
            m = macro.get(n[4], "Container")
            if m in ("Person", "System_Ext"):
                descr = " ".join(x for x in (n[2], n[3]) if x)
                out.append(f'  {m}({n[0]}, "{n[1]}", "{descr}")')
            else:
                out.append(f'  {m}({n[0]}, "{n[1]}", "{n[2]}", "{n[3]}")')
        out.append("}")
    out.append("")
    for a, b, text, is_async in EDGES:
        parts = text.split(" ", 1)
        tags = ', $tags="async"' if is_async else ""
        out.append(f'Rel({a}, {b}, "{parts[1] if len(parts) > 1 else parts[0]}", "{parts[0]}"{tags})')
    out += ["", "SHOW_LEGEND()", "@enduml"]
    return "\n".join(out) + "\n"


# ---------------------------------------------------------------- Structurizr DSL
def structurizr():
    tag = {"database": "Database", "queue": "Queue", "external": "External"}
    out = [f'workspace "{TITLE}" "Retail payments: card payments, fraud checks and settlement." {{', "", "  model {"]
    out.append('    group "Internet" {')
    for n in NODES:
        if n[5] == "internet":
            if n[4] == "user":
                out.append(f'      {n[0]} = person "{n[1]}" "{n[2]}"')
            else:
                out.append(f'      {n[0]} = softwareSystem "{n[1]}" "{n[2]}" "External"')
    out.append("    }")
    out.append(f'    northbank = softwareSystem "{TITLE}" {{')
    for zid, zname in ZONES[1:]:
        out.append(f'      group "{zname}" {{')
        for n in [n for n in NODES if n[5] == zid]:
            t = tag.get(n[4], "")
            out.append(f'        {n[0]} = container "{n[1]}" "{n[3]}" "{n[2]}"' + (f' "{t}"' if t else ""))
        out.append("      }")
    out.append("    }")
    out.append("")
    for a, b, text, is_async in EDGES:
        parts = text.split(" ", 1)
        extra = ' "Async"' if is_async else ""
        out.append(f'    {a} -> {b} "{parts[1] if len(parts) > 1 else parts[0]}" "{parts[0]}"{extra}')
    out += ["  }", "", "  views {", "    container northbank {", "      include *", "      autolayout lr", "    }",
            '    styles {', '      element "Database" {', "        shape cylinder", "      }",
            '      element "Queue" {', "        shape pipe", "      }", "    }", "  }", "}"]
    return "\n".join(out) + "\n"


# ---------------------------------------------------------------- Excalidraw
def excalidraw():
    els = []
    seed = [1000]

    def base(eid, etype, x, y, w, h, **kw):
        seed[0] += 1
        e = {"id": eid, "type": etype, "x": x, "y": y, "width": w, "height": h, "angle": 0,
             "strokeColor": "#1e1e1e", "backgroundColor": "transparent", "fillStyle": "solid", "strokeWidth": 2,
             "strokeStyle": "solid", "roughness": 1, "opacity": 100, "groupIds": [], "frameId": None,
             "roundness": {"type": 3}, "seed": seed[0], "version": 1, "versionNonce": seed[0] * 7,
             "isDeleted": False, "boundElements": [], "updated": 1760000000000, "link": None, "locked": False}
        e.update(kw)
        return e

    def text(eid, x, y, w, h, s, container=None, size=16):
        return base(eid, "text", x, y, w, h, text=s, originalText=s, fontSize=size, fontFamily=1,
                    textAlign="center" if container else "left", verticalAlign="middle" if container else "top",
                    containerId=container, autoResize=True, lineHeight=1.25, roundness=None)

    fills = {"database": "#ffc9c9", "queue": "#ffec99", "external": "#e9ecef", "user": "#d0ebff", "gateway": "#a5d8ff"}
    for zid, zname in ZONES:
        x, y, w, h = zone_box(zid)
        els.append(base(f"zone-{zid}", "rectangle", x, y, w, h, strokeStyle="dashed", strokeColor="#868e96", roundness=None))
        els.append(text(f"zone-{zid}-title", x + 10, y + 6, 160, 20, zname, size=18))
    for n in NODES:
        x, y = pos(n)
        kind = "ellipse" if n[4] in ("user", "database") else "rectangle"
        shape = base(n[0], kind, x, y, W, H, backgroundColor=fills.get(n[4], "#b2f2bb"),
                     boundElements=[{"id": f"{n[0]}-label", "type": "text"}])
        els.append(shape)
        els.append(text(f"{n[0]}-label", x + 10, y + 15, W - 20, H - 30, label(n, "\n"), container=n[0]))
    for i, (a, b, s, is_async) in enumerate(EDGES, 1):
        ax, ay = pos(node(a)); bx, by = pos(node(b))
        sx, sy = ax + W, ay + H / 2
        tx, ty = bx, by + H / 2
        if bx <= ax:  # pointing back left: leave from the left edge
            sx, tx = ax, bx + W
        if bx == ax:  # same column: vertical
            sx, sy, tx, ty = ax + W / 2, ay + H, bx + W / 2, by
        arrow = base(f"arrow{i}", "arrow", sx, sy, abs(tx - sx), abs(ty - sy), points=[[0, 0], [tx - sx, ty - sy]],
                     strokeStyle="dashed" if is_async else "solid", roundness={"type": 2},
                     startBinding={"elementId": a, "focus": 0, "gap": 4}, endBinding={"elementId": b, "focus": 0, "gap": 4},
                     startArrowhead=None, endArrowhead="arrow", lastCommittedPoint=None,
                     boundElements=[{"id": f"arrow{i}-label", "type": "text"}])
        els.append(arrow)
        els.append(text(f"arrow{i}-label", (sx + tx) / 2 - 60, (sy + ty) / 2 - 10, 120, 20, s, container=f"arrow{i}", size=14))
        for e in els:
            if e["id"] in (a, b):
                e["boundElements"].append({"id": f"arrow{i}", "type": "arrow"})
    doc = {"type": "excalidraw", "version": 2, "source": "https://excalidraw.com",
           "elements": els, "appState": {"gridSize": None, "viewBackgroundColor": "#ffffff"}, "files": {}}
    return json.dumps(doc, indent=2) + "\n"


# ---------------------------------------------------------------- Gliffy
def gliffy():
    objs = []
    oid = [0]
    ids = {}

    def text_child(s, w, h):
        return {"x": 2, "y": 0, "rotation": 0, "id": None, "width": w - 4, "height": h, "uid": None, "order": "auto",
                "lockAspectRatio": False, "lockShape": False,
                "graphic": {"type": "Text", "Text": {"tid": None, "valign": "middle", "overflow": "none", "vposition": "none", "hposition": "none",
                                                      "html": f'<p style="text-align:center;"><span style="font-family:Arial;font-size:12px;">{html.escape(s)}</span></p>',
                                                      "paddingLeft": 2, "paddingRight": 2, "paddingBottom": 2, "paddingTop": 2}},
                "children": None}

    uids = {"database": "com.gliffy.shape.basic.basic_v1.default.cylinder", "user": "com.gliffy.shape.uml.uml_v2.use_case.actor",
            "external": "com.gliffy.shape.network.network_v4.business.cloud", "queue": "com.gliffy.shape.flowchart.flowchart_v1.default.data_storage"}
    for zid, zname in ZONES:
        x, y, w, h = zone_box(zid)
        ids[f"zone-{zid}"] = oid[0]
        objs.append({"x": x, "y": y, "rotation": 0, "id": oid[0], "width": w, "height": h,
                     "uid": "com.gliffy.shape.basic.basic_v1.default.rectangle", "order": oid[0], "lockAspectRatio": False, "lockShape": False,
                     "graphic": {"type": "Shape", "Shape": {"tid": "com.gliffy.stencil.rectangle.basic_v1", "strokeWidth": 1, "strokeColor": "#999999",
                                                             "fillColor": "none", "dashStyle": "4.0,4.0", "gradient": False, "dropShadow": False, "state": 0, "shadowX": 0, "shadowY": 0, "opacity": 1}},
                     "children": [dict(text_child(zname, 160, 22), **{"y": 4})]})
        oid[0] += 1
    for n in NODES:
        x, y = pos(n)
        ids[n[0]] = oid[0]
        uid = uids.get(n[4], "com.gliffy.shape.basic.basic_v1.default.rectangle")
        objs.append({"x": x, "y": y, "rotation": 0, "id": oid[0], "width": W, "height": H, "uid": uid, "order": oid[0],
                     "lockAspectRatio": False, "lockShape": False,
                     "graphic": {"type": "Shape", "Shape": {"tid": uid.replace("shape", "stencil"), "strokeWidth": 2, "strokeColor": "#333333",
                                                             "fillColor": "#FFFFFF", "gradient": False, "dropShadow": False, "state": 0, "shadowX": 0, "shadowY": 0, "opacity": 1}},
                     "children": [text_child(label(n), W, H)]})
        oid[0] += 1
    for a, b, s, is_async in EDGES:
        ax, ay = pos(node(a)); bx, by = pos(node(b))
        objs.append({"x": 0, "y": 0, "rotation": 0, "id": oid[0], "width": 100, "height": 100,
                     "uid": "com.gliffy.shape.basic.basic_v1.default.line", "order": oid[0], "lockAspectRatio": False, "lockShape": False,
                     "constraints": {"constraints": [],
                                     "startConstraint": {"type": "StartPositionConstraint", "StartPositionConstraint": {"nodeId": ids[a], "py": 0.5, "px": 1}},
                                     "endConstraint": {"type": "EndPositionConstraint", "EndPositionConstraint": {"nodeId": ids[b], "py": 0.5, "px": 0}}},
                     "graphic": {"type": "Line", "Line": {"strokeWidth": 2, "strokeColor": "#000000", "fillColor": "none",
                                                           "dashStyle": "8.0,2.0" if is_async else None, "startArrow": 0, "endArrow": 1,
                                                           "startArrowRotation": "auto", "endArrowRotation": "auto", "interpolationType": "linear",
                                                           "cornerRadius": None, "ortho": False,
                                                           "controlPath": [[ax + W, ay + H / 2], [bx, by + H / 2]], "lockSegments": {}}},
                     "children": [dict(text_child(s, 120, 20), **{"x": 0, "y": 0})]})
        oid[0] += 1
    doc = {"contentType": "application/gliffy+json", "version": "1.3",
           "stage": {"background": "#FFFFFF", "width": 1200, "height": 700, "nodeIndex": oid[0], "autoFit": True,
                     "exportBorder": False, "gridOn": True, "snapToGrid": True, "drawingGuidesOn": True, "pageBreaksOn": False,
                     "printGridOn": False, "printPaper": "LETTER", "printShrinkToFit": False, "printPortrait": False,
                     "maxWidth": 5000, "maxHeight": 5000, "themeData": None, "viewportType": "default", "fitBB": None,
                     "objects": objs, "layers": [{"guid": "layer0", "order": 0, "name": "Layer 0", "active": True, "locked": False, "visible": True, "nodeIndex": oid[0]}],
                     "shapeStyles": {}, "lineStyles": {}, "textStyles": {}},
           "metadata": {"title": TITLE, "revision": 0, "exportBorder": False, "loadPosition": "default", "libraries": []},
           "embeddedResources": {"index": 0, "resources": []}}
    return json.dumps(doc, indent=2) + "\n"


# ---------------------------------------------------------------- SVG (as exported by Figma / any design tool)
def svg():
    fills = {"database": "#ffc9c9", "queue": "#ffec99", "external": "#e9ecef", "user": "#d0ebff", "gateway": "#a5d8ff"}
    out = ['<svg xmlns="http://www.w3.org/2000/svg" width="1180" height="620" viewBox="0 0 1180 620" font-family="Inter, Arial, sans-serif">',
           '  <defs><marker id="arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">'
           '<path d="M0,0 L10,5 L0,10 z" fill="#333"/></marker></defs>',
           '  <rect width="1180" height="620" fill="#ffffff"/>',
           f'  <text x="40" y="34" font-size="20" font-weight="700" fill="#111">{TITLE}</text>']
    for zid, zname in ZONES:
        x, y, w, h = zone_box(zid)
        out.append(f'  <g id="zone-{zid}"><rect x="{x}" y="{y}" width="{w}" height="{h}" rx="8" fill="none" stroke="#999" stroke-dasharray="6 4"/>'
                   f'<text x="{x + 10}" y="{y + 18}" font-size="13" font-weight="600" fill="#555">{zname}</text></g>')
    for a, b, s, is_async in EDGES:
        ax, ay = pos(node(a)); bx, by = pos(node(b))
        sx, sy, tx, ty = ax + W, ay + H / 2, bx, by + H / 2
        if bx < ax:
            sx, tx = ax, bx + W
        if bx == ax:
            sx, sy, tx, ty = ax + W / 2, (ay + H if by > ay else ay), bx + W / 2, (by if by > ay else by + H)
        dash = ' stroke-dasharray="5 4"' if is_async else ""
        out.append(f'  <g id="flow-{a}-{b}"><line x1="{sx}" y1="{sy}" x2="{tx}" y2="{ty}" stroke="#333" stroke-width="1.6"{dash} marker-end="url(#arrow)"/>'
                   f'<text x="{(sx + tx) / 2}" y="{(sy + ty) / 2 - 6}" font-size="11" text-anchor="middle" fill="#333">{html.escape(s)}</text></g>')
    for n in NODES:
        x, y = pos(n)
        lines = [n[1]] + [s for s in (f"({n[2]})" if n[2] else "", n[3]) if s]
        tspans = "".join(f'<tspan x="{x + W / 2}" dy="{0 if i == 0 else 16}">{html.escape(t)}</tspan>' for i, t in enumerate(lines))
        shape = (f'<ellipse cx="{x + W / 2}" cy="{y + H / 2}" rx="{W / 2}" ry="{H / 2}" fill="{fills.get(n[4], "#d3f9d8")}" stroke="#333"/>'
                 if n[4] in ("user", "database") else
                 f'<rect x="{x}" y="{y}" width="{W}" height="{H}" rx="10" fill="{fills.get(n[4], "#d3f9d8")}" stroke="#333"/>')
        out.append(f'  <g id="{n[0]}">{shape}<text x="{x + W / 2}" y="{y + H / 2 - 6 - 8 * (len(lines) - 2)}" font-size="13" text-anchor="middle" fill="#111">{tspans}</text></g>')
    out.append("</svg>")
    return "\n".join(out) + "\n"


# ---------------------------------------------------------------- Visio .vsdx
NS = "http://schemas.microsoft.com/office/visio/2012/main"
RNS = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
INCH = 96.0  # px per inch


def vsdx(path):
    masters = [("2", "Rectangle"), ("3", "Database"), ("4", "User"), ("5", "Queue"), ("6", "Cloud"), ("7", "Dynamic connector"), ("8", "Container")]
    master_of = {"database": "3", "user": "4", "queue": "5", "external": "6"}
    page_h = 7.5

    def cells(**kv):
        return "".join(f'<Cell N="{k}" V="{v}"/>' for k, v in kv.items())

    def rect_geometry():
        return ('<Section N="Geometry" IX="0"><Cell N="NoFill" V="0"/><Cell N="NoLine" V="0"/>'
                '<Row T="RelMoveTo" IX="1"><Cell N="X" V="0"/><Cell N="Y" V="0"/></Row>'
                '<Row T="RelLineTo" IX="2"><Cell N="X" V="1"/><Cell N="Y" V="0"/></Row>'
                '<Row T="RelLineTo" IX="3"><Cell N="X" V="1"/><Cell N="Y" V="1"/></Row>'
                '<Row T="RelLineTo" IX="4"><Cell N="X" V="0"/><Cell N="Y" V="1"/></Row>'
                '<Row T="RelLineTo" IX="5"><Cell N="X" V="0"/><Cell N="Y" V="0"/></Row></Section>')

    shapes, connects = [], []
    sid = 1
    ids = {}
    # Zones are Visio containers: a rectangle whose Relationships cell lists its members.
    zone_ids = {}
    for zid, _ in ZONES:
        zone_ids[zid] = sid; sid += 1
    for n in NODES:
        ids[n[0]] = sid; sid += 1
    for zid, zname in ZONES:
        x, y, w, h = zone_box(zid)
        members = ",".join(f"Sheet.{ids[n[0]]}!SheetRef()" for n in NODES if n[5] == zid)
        shapes.append(f'<Shape ID="{zone_ids[zid]}" NameU="Container.{zone_ids[zid]}" Name="Container" Type="Shape" Master="8">'
                      + cells(PinX=round((x + w / 2) / INCH, 4), PinY=round(page_h - (y + h / 2) / INCH, 4), Width=round(w / INCH, 4), Height=round(h / INCH, 4),
                              LocPinX=round(w / INCH / 2, 4), LocPinY=round(h / INCH / 2, 4), LinePattern=2, FillPattern=0, VerticalAlign=0)
                      + f'<Cell N="Relationships" V="0" F="SUM(DEPENDSON(1,{members}))"/>'
                      + '<Section N="User"><Row N="msvStructureType"><Cell N="Value" V="Container" U="STR"/></Row></Section>'
                      + rect_geometry() + f'<Text>{html.escape(zname)}</Text></Shape>')
    for n in NODES:
        x, y = pos(n)
        shapes.append(f'<Shape ID="{ids[n[0]]}" NameU="{masters[int(master_of.get(n[4], "2")) - 2][1]}.{ids[n[0]]}" Type="Shape" Master="{master_of.get(n[4], "2")}">'
                      + cells(PinX=round((x + W / 2) / INCH, 4), PinY=round(page_h - (y + H / 2) / INCH, 4), Width=round(W / INCH, 4), Height=round(H / INCH, 4),
                              LocPinX=round(W / INCH / 2, 4), LocPinY=round(H / INCH / 2, 4))
                      + rect_geometry() + f'<Text>{html.escape(label(n))}</Text></Shape>')
    for a, b, text, is_async in EDGES:
        sx, sy, tx, ty = endpoints(a, b)
        # Leftward lines are drawn right-to-left with the arrowhead at the start, so their label isn't upside down
        # (Visio files do this too; the parser reads BeginArrow/EndArrow to get the direction back).
        backwards = tx < sx
        if backwards:
            sx, sy, tx, ty = tx, ty, sx, sy
        bxi, byi = sx / INCH, page_h - sy / INCH
        exi, eyi = tx / INCH, page_h - ty / INCH
        shapes.append(f'<Shape ID="{sid}" NameU="Dynamic connector.{sid}" Name="Dynamic connector" Type="Shape" Master="7">'
                      + f'<Cell N="BeginX" V="{round(bxi, 4)}" F="_WALKGLUE(BegTrigger,EndTrigger,WalkPreference)"/>'
                      + f'<Cell N="BeginY" V="{round(byi, 4)}" F="_WALKGLUE(BegTrigger,EndTrigger,WalkPreference)"/>'
                      + f'<Cell N="EndX" V="{round(exi, 4)}" F="_WALKGLUE(EndTrigger,BegTrigger,WalkPreference)"/>'
                      + f'<Cell N="EndY" V="{round(eyi, 4)}" F="_WALKGLUE(EndTrigger,BegTrigger,WalkPreference)"/>'
                      # A 1-D shape sits at the midpoint of its ends, as long as the line, rotated to its angle.
                      + cells(PinX=round((bxi + exi) / 2, 4), PinY=round((byi + eyi) / 2, 4),
                              Width=round(math.hypot(exi - bxi, eyi - byi), 4), Height=0,
                              LocPinX=round(math.hypot(exi - bxi, eyi - byi) / 2, 4), LocPinY=0,
                              Angle=round(math.atan2(eyi - byi, exi - bxi), 6),
                              LinePattern=2 if is_async else 1, EndArrow=0 if backwards else 13, BeginArrow=13 if backwards else 0, ObjType=2)
                      + '<Section N="Geometry" IX="0"><Row T="MoveTo" IX="1"><Cell N="X" V="0"/><Cell N="Y" V="0"/></Row>'
                      + f'<Row T="LineTo" IX="2"><Cell N="X" V="{round(math.hypot(exi - bxi, eyi - byi), 4)}"/><Cell N="Y" V="0"/></Row></Section>'
                      + f'<Text>{html.escape(text)}</Text></Shape>')
        begin_shape, end_shape = (b, a) if backwards else (a, b)
        connects.append(f'<Connect FromSheet="{sid}" FromCell="BeginX" FromPart="9" ToSheet="{ids[begin_shape]}" ToCell="PinX" ToPart="3"/>')
        connects.append(f'<Connect FromSheet="{sid}" FromCell="EndX" FromPart="12" ToSheet="{ids[end_shape]}" ToCell="PinX" ToPart="3"/>')
        sid += 1

    page = (f'<?xml version="1.0" encoding="utf-8"?>\n<PageContents xmlns="{NS}" xmlns:r="{RNS}" xml:space="preserve">'
            f'<Shapes>{"".join(shapes)}</Shapes><Connects>{"".join(connects)}</Connects></PageContents>')
    master_xml = "".join(
        f'<Master ID="{mid}" NameU="{name}" Name="{name}" IsCustomNameU="1" IsCustomName="1" Prompt="" IconSize="1" AlignName="2" MatchByName="0" IconUpdate="1" UniqueID="{{{uuid.uuid5(uuid.NAMESPACE_DNS, name)}}}" BaseID="{{{uuid.uuid5(uuid.NAMESPACE_URL, name)}}}" PatternFlags="0" Hidden="0" MasterType="2">'
        f'<PageSheet LineStyle="0" FillStyle="0" TextStyle="0"><Cell N="PageWidth" V="1"/><Cell N="PageHeight" V="1"/></PageSheet><Rel r:id="rId{mid}"/></Master>'
        for mid, name in masters)
    master_rels = "".join(f'<Relationship Id="rId{mid}" Type="http://schemas.microsoft.com/visio/2010/relationships/master" Target="master{mid}.xml"/>' for mid, _ in masters)

    def master_contents(name):
        one_d = name == "Dynamic connector"
        geo = ('<Section N="Geometry" IX="0"><Row T="MoveTo" IX="1"><Cell N="X" V="0"/><Cell N="Y" V="0"/></Row><Row T="LineTo" IX="2"><Cell N="X" V="1"/><Cell N="Y" V="0"/></Row></Section>'
               if one_d else rect_geometry())
        return (f'<?xml version="1.0" encoding="utf-8"?>\n<MasterContents xmlns="{NS}" xmlns:r="{RNS}" xml:space="preserve"><Shapes>'
                f'<Shape ID="5" Type="Shape" LineStyle="0" FillStyle="0" TextStyle="0">{cells(Width=1, Height=0 if one_d else 1, PinX=0.5, PinY=0 if one_d else 0.5)}{geo}</Shape>'
                '</Shapes></MasterContents>')

    files = {
        "[Content_Types].xml":
            '<?xml version="1.0" encoding="utf-8"?>\n<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
            '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
            '<Default Extension="xml" ContentType="application/xml"/>'
            '<Override PartName="/visio/document.xml" ContentType="application/vnd.ms-visio.drawing.main+xml"/>'
            '<Override PartName="/visio/masters/masters.xml" ContentType="application/vnd.ms-visio.masters+xml"/>'
            + "".join(f'<Override PartName="/visio/masters/master{mid}.xml" ContentType="application/vnd.ms-visio.master+xml"/>' for mid, _ in masters)
            + '<Override PartName="/visio/pages/pages.xml" ContentType="application/vnd.ms-visio.pages+xml"/>'
            '<Override PartName="/visio/pages/page1.xml" ContentType="application/vnd.ms-visio.page+xml"/>'
            '<Override PartName="/visio/windows.xml" ContentType="application/vnd.ms-visio.windows+xml"/>'
            '<Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>'
            '<Override PartName="/docProps/app.xml" ContentType="application/vnd.openxmlformats-officedocument.extended-properties+xml"/></Types>',
        "_rels/.rels":
            '<?xml version="1.0" encoding="utf-8"?>\n<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
            '<Relationship Id="rId1" Type="http://schemas.microsoft.com/visio/2010/relationships/document" Target="visio/document.xml"/>'
            '<Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>'
            '<Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties" Target="docProps/app.xml"/></Relationships>',
        "docProps/core.xml":
            '<?xml version="1.0" encoding="utf-8"?>\n<cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" '
            'xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">'
            f'<dc:title>{TITLE}</dc:title><dc:creator>SecureFlow sample generator</dc:creator></cp:coreProperties>',
        "docProps/app.xml":
            '<?xml version="1.0" encoding="utf-8"?>\n<Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"><Application>Microsoft Visio</Application></Properties>',
        "visio/document.xml":
            f'<?xml version="1.0" encoding="utf-8"?>\n<VisioDocument xmlns="{NS}" xmlns:r="{RNS}" xml:space="preserve">'
            '<DocumentSettings TopPage="0" DefaultTextStyle="0" DefaultLineStyle="0" DefaultFillStyle="0" DefaultGuideStyle="0"/>'
            '<Colors><ColorEntry IX="0" RGB="#000000"/><ColorEntry IX="1" RGB="#FFFFFF"/></Colors>'
            '<FaceNames><FaceName NameU="Calibri" CharSets="1" Panose="2 15 5 2 2 2 4 3 2 4" Flags="325"/></FaceNames>'
            '<StyleSheets><StyleSheet ID="0" NameU="No Style" Name="No Style">'
            + cells(EnableLineProps=1, EnableFillProps=1, EnableTextProps=1, LineWeight=0.01041666666666667, LineColor=0, LinePattern=1,
                    FillForegnd=1, FillPattern=1, CharFont=1, CharSize=0.1666666666666667, CharColor=0, VerticalAlign=1)
            + '</StyleSheet></StyleSheets></VisioDocument>',
        "visio/_rels/document.xml.rels":
            '<?xml version="1.0" encoding="utf-8"?>\n<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
            '<Relationship Id="rId1" Type="http://schemas.microsoft.com/visio/2010/relationships/masters" Target="masters/masters.xml"/>'
            '<Relationship Id="rId2" Type="http://schemas.microsoft.com/visio/2010/relationships/pages" Target="pages/pages.xml"/>'
            '<Relationship Id="rId3" Type="http://schemas.microsoft.com/visio/2010/relationships/windows" Target="windows.xml"/></Relationships>',
        "visio/windows.xml":
            f'<?xml version="1.0" encoding="utf-8"?>\n<Windows xmlns="{NS}" xmlns:r="{RNS}" ClientWidth="1400" ClientHeight="800" xml:space="preserve">'
            '<Window ID="0" WindowType="Drawing" WindowState="1073741824" ViewScale="-1" ViewCenterX="5.5" ViewCenterY="3.75" Page="0"/></Windows>',
        "visio/masters/masters.xml":
            f'<?xml version="1.0" encoding="utf-8"?>\n<Masters xmlns="{NS}" xmlns:r="{RNS}" xml:space="preserve">{master_xml}</Masters>',
        "visio/masters/_rels/masters.xml.rels":
            f'<?xml version="1.0" encoding="utf-8"?>\n<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">{master_rels}</Relationships>',
        "visio/pages/pages.xml":
            f'<?xml version="1.0" encoding="utf-8"?>\n<Pages xmlns="{NS}" xmlns:r="{RNS}" xml:space="preserve">'
            f'<Page ID="0" NameU="{TITLE}" Name="{TITLE}" ViewScale="-1" ViewCenterX="5.5" ViewCenterY="3.75"><PageSheet LineStyle="0" FillStyle="0" TextStyle="0">'
            + cells(PageWidth=12.5, PageHeight=page_h, PageScale=1, DrawingScale=1, DrawingSizeType=0, DrawingScaleType=0)
            + '</PageSheet><Rel r:id="rId1"/></Page></Pages>',
        "visio/pages/_rels/pages.xml.rels":
            '<?xml version="1.0" encoding="utf-8"?>\n<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
            '<Relationship Id="rId1" Type="http://schemas.microsoft.com/visio/2010/relationships/page" Target="page1.xml"/></Relationships>',
        "visio/pages/page1.xml": page,
    }
    for mid, name in masters:
        files[f"visio/masters/master{mid}.xml"] = master_contents(name)
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        for name, content in files.items():
            z.writestr(name, content)


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "..", "samples", "diagrams")
    os.makedirs(out, exist_ok=True)
    text_files = {
        "northbank.drawio": drawio(), "northbank.mmd": mermaid(), "northbank-c4.puml": plantuml(), "northbank.dsl": structurizr(),
        "northbank.excalidraw": excalidraw(), "northbank.gliffy": gliffy(), "northbank.svg": svg(),
    }
    for name, content in text_files.items():
        with open(os.path.join(out, name), "w", encoding="utf-8", newline="\n") as f:
            f.write(content)
    vsdx(os.path.join(out, "northbank.vsdx"))
    for name in list(text_files) + ["northbank.vsdx"]:
        print("wrote", os.path.join(out, name))


if __name__ == "__main__":
    main()
