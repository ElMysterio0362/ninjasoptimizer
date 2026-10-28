"""
Gera src/NinjaOptimizer.App/Theme/Icons.xaml a partir dos ícones do sistema de design
(contorno 1,75 px, grade 24, pontas arredondadas). Retângulos e círculos do SVG viram path.

    python build/brand/generate_icons.py
"""
from pathlib import Path

def rect(x, y, w, h, r=0):
    if r == 0:
        return f"M{x},{y} H{x+w} V{y+h} H{x} Z"
    return (f"M{x+r},{y} H{x+w-r} A{r},{r} 0 0 1 {x+w},{y+r} V{y+h-r} A{r},{r} 0 0 1 {x+w-r},{y+h} "
            f"H{x+r} A{r},{r} 0 0 1 {x},{y+h-r} V{y+r} A{r},{r} 0 0 1 {x+r},{y} Z")

def circle(cx, cy, r):
    return f"M{cx-r},{cy} A{r},{r} 0 1 0 {cx+r},{cy} A{r},{r} 0 1 0 {cx-r},{cy} Z"

ICONS = {
    # Do sistema de design (06 · Sistema de UI)
    "Dashboard": [rect(3, 3, 7, 9, 2), rect(14, 3, 7, 5, 2), rect(14, 12, 7, 9, 2), rect(3, 16, 7, 5, 2)],
    "Bolt": ["M13,2 L4,14 H11 L10,22 L19,10 H12 Z"],
    "List": ["M8,6 H21 M8,12 H21 M8,18 H21 M3.5,6 H3.51 M3.5,12 H3.51 M3.5,18 H3.51"],
    "Cpu": [rect(6, 6, 12, 12, 2), rect(9.5, 9.5, 5, 5, 1), "M9,2 V6 M15,2 V6 M9,18 V22 M15,18 V22 M2,9 H6 M2,15 H6 M18,9 H22 M18,15 H22"],
    "Gpu": [rect(2, 6, 20, 11, 2), circle(9, 11.5, 2.5), "M15,9.5 H19 M15,13.5 H19 M5,20 H19"],
    "Memory": [rect(2, 5, 20, 11, 2), "M6,9.5 H9 M11,9.5 H14 M16,9.5 H18 M5,16 V19 M9,16 V19 M13,16 V19 M17,16 V19"],
    "Disk": [rect(3, 13, 18, 7, 2), "M5,13 L7.5,5 H16.5 L19,13 M7.5,16.5 H7.51 M11,16.5 H16"],
    "Network": ["M2,9 A15,15 0 0 1 22,9 M5.5,12.5 A10,10 0 0 1 18.5,12.5 M9,16 A5,5 0 0 1 15,16 M12,19.5 H12.01"],
    "Benchmark": ["M4.5,18 A9,9 0 1 1 19.5,18 M12,14 L16,9", circle(12, 14.5, 1)],
    "Overlay": [rect(2.5, 4, 19, 13, 2), "M8,21 H16 M12,17 V21 M6.5,13 L9.5,10 L12.5,12 L17,7.5"],
    "Filter": ["M3,4 H21 L14,12.5 V19 L10,21 V12.5 Z"],
    "Settings": ["M4,7 H14 M18,7 H20 M4,17 H6 M10,17 H20", circle(16, 7, 2), circle(8, 17, 2)],
    # Avisos (11 · Tom de voz)
    "CheckCircle": [circle(12, 12, 9), "M8,12.5 L10.7,15.2 L16,9.8"],
    "Warning": ["M12,3 L22,21 H2 Z M12,10 V14 M12,17.5 H12.01"],
    "Error": [circle(12, 12, 9), "M12,7.5 V12.5 M12,16 H12.01"],
    # Complementares, no mesmo traço
    "Check": ["M5,12.5 L9.5,17 L19,7"],
    "Info": [circle(12, 12, 9), "M12,11 V16 M12,7.5 H12.01"],
    "Undo": ["M9,14 L4,9 L9,4 M4,9 H14.5 A5.5,5.5 0 0 1 14.5,20 H11"],
    "Refresh": ["M20,12 A8,8 0 1 1 17.7,6.3 M20,4 V9 H15"],
    "Export": ["M12,3 V15 M7,8 L12,3 L17,8 M5,15 V19 A2,2 0 0 0 7,21 H17 A2,2 0 0 0 19,19 V15"],
    "Download": ["M12,4 V15 M7,10 L12,15 L17,10 M5,20 H19"],
    "Shield": ["M12,3 L20,6 V12 C20,16.5 16.6,20.3 12,21 C7.4,20.3 4,16.5 4,12 V6 Z"],
    "Lock": [rect(5, 11, 14, 10, 2), "M8,11 V7 A4,4 0 0 1 16,7 V11"],
    "Gamepad": [rect(2, 7, 20, 11, 4), "M7,12.5 H11 M9,10.5 V14.5 M15.5,12 H15.51 M18,14 H18.01"],
    "Monitor": [rect(3, 4, 18, 13, 2), "M8,21 H16 M12,17 V21"],
    "Board": [rect(3, 3, 18, 18, 2), rect(7, 7, 5, 5, 1), "M15,7 H17 M15,11 H17 M7,16 H17"],
    "Power": ["M12,3 V11 M7.5,6.5 A7,7 0 1 0 16.5,6.5"],
    "Clock": [circle(12, 12, 9), "M12,7 V12 L15,14"],
    "Folder": ["M3,7 A2,2 0 0 1 5,5 H9 L11,7 H19 A2,2 0 0 1 21,9 V17 A2,2 0 0 1 19,19 H5 A2,2 0 0 1 3,17 Z"],
    "File": ["M14,3 H7 A2,2 0 0 0 5,5 V19 A2,2 0 0 0 7,21 H17 A2,2 0 0 0 19,19 V8 Z M14,3 V8 H19"],
    "Minimize": ["M6,12 H18"],
    "Maximize": [rect(5.5, 5.5, 13, 13, 2)],
    "Close": ["M6,6 L18,18 M18,6 L6,18"],
}

def main():
    root = Path(__file__).resolve().parents[2]
    out = root / "src" / "NinjaOptimizer.App" / "Theme" / "Icons.xaml"
    lines = ['<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
             '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
             '    <!-- GERADO por build/brand/generate_icons.py: edite o script, não este arquivo. -->',
             '    <!-- Ícones de contorno, grade 24 px. Use com <c:Icon Data="{StaticResource Icon.Cpu}" />. -->']
    for name, parts in ICONS.items():
        lines.append(f'    <Geometry x:Key="Icon.{name}">{" ".join(parts)}</Geometry>')
    lines += [
        '',
        '    <!-- Símbolo da marca (shuriken), grade 64, com furo central (regra par-ímpar). -->',
        '    <Geometry x:Key="Brand.Shuriken">F0 M32,3 L40,21.8 L61,32 L42.2,40 L32,61 L24,42.2 L3,32 L21.8,24 Z '
        + circle(32, 32, 4.5) + '</Geometry>',
        '    <!-- Versão sem furo, para tamanhos abaixo de 24 px. -->',
        '    <Geometry x:Key="Brand.ShurikenSolid">M32,3 L40,21.8 L61,32 L42.2,40 L32,61 L24,42.2 L3,32 L21.8,24 Z</Geometry>',
        '</ResourceDictionary>', '']
    out.write_text("\n".join(lines), encoding="utf-8")
    print(f"{len(ICONS)} ícones → {out}")

if __name__ == "__main__":
    main()
