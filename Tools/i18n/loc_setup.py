# -*- coding: utf-8 -*-
"""Round seven: the Center installer window and the wizard header.

These are XAML literals and direct .Text assignments, so they never reached a builder - they are
now set from code and translated at the point they hit the screen."""
SETUP = {
"Add a desktop icon": (u"Desktop-Verknüpfung erstellen", u"Ajouter une icône au bureau",
                       u"바탕 화면 아이콘 추가", u"Añadir icono al escritorio"),
"Guided setup":       (u"Geführte Einrichtung", u"Installation guidée",
                       u"단계별 설치", u"Instalación guiada"),
"Updating...":        (u"Aktualisiere...", u"Mise à jour...", u"업데이트 중...", u"Actualizando..."),
"Installing...":      (u"Installiere...", u"Installation...", u"설치 중...", u"Instalando..."),
"Relaunching from install location...":
    (u"Neustart vom Installationsort...", u"Redémarrage depuis l'installation...",
     u"설치 위치에서 다시 시작...", u"Reiniciando desde la instalación..."),
"No uninstaller found for the older version.":
    (u"Kein Deinstallationsprogramm für die ältere Version gefunden.",
     u"Aucun désinstalleur trouvé pour l'ancienne version.",
     u"이전 버전의 제거 프로그램을 찾지 못했습니다.",
     u"No se encontró desinstalador de la versión antigua."),
"This version is already installed. Open it from the Start Menu or the ClawTweaks Game Bar widget instead of running this Setup file again.":
    (u"Diese Version ist bereits installiert. Öffne sie über das Startmenü oder das ClawTweaks-Widget in der Game Bar, statt diese Setup-Datei erneut auszuführen.",
     u"Cette version est déjà installée. Ouvrez-la depuis le menu Démarrer ou le widget ClawTweaks de la Game Bar plutôt que de relancer ce fichier.",
     u"이 버전은 이미 설치되어 있습니다. 이 설치 파일을 다시 실행하지 말고 시작 메뉴나 Game Bar의 ClawTweaks 위젯에서 여세요.",
     u"Esta versión ya está instalada. Ábrela desde el menú Inicio o el widget de ClawTweaks en la Game Bar en vez de ejecutar este archivo otra vez."),
"Update failed — see the log for details. Try again, or run as Administrator.":
    (u"Update fehlgeschlagen — Details stehen im Log. Erneut versuchen oder als Administrator ausführen.",
     u"Échec de la mise à jour — voir le journal. Réessayez, ou lancez en administrateur.",
     u"업데이트 실패 — 자세한 내용은 로그를 보세요. 다시 시도하거나 관리자 권한으로 실행하세요.",
     u"Fallo al actualizar — mira el registro. Inténtalo otra vez o ejecuta como administrador."),
"Install failed — see the log for details. Try again, or run as Administrator.":
    (u"Installation fehlgeschlagen — Details stehen im Log. Erneut versuchen oder als Administrator ausführen.",
     u"Échec de l'installation — voir le journal. Réessayez, ou lancez en administrateur.",
     u"설치 실패 — 자세한 내용은 로그를 보세요. 다시 시도하거나 관리자 권한으로 실행하세요.",
     u"Fallo al instalar — mira el registro. Inténtalo otra vez o ejecuta como administrador."),
}
