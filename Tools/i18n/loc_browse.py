# -*- coding: utf-8 -*-
"""Round eight: the Update & Release screen.

VOCABULARY DECISION, applied in all four languages at once: the user-facing word is VERSION, never
"build" and never "release". Those are developer words; somebody choosing what to install is
choosing a version. The channel names follow the same rule - main / test / experimental."""
BROWSE = {

# ---- the three channels ---------------------------------------------------
"Main versions":     (u"Hauptversionen", u"Versions principales", u"주요 버전", u"Versiones principales"),
"Recommended for everyday use": (u"Empfohlen für den täglichen Gebrauch",
                                 u"Recommandé pour un usage quotidien",
                                 u"일상적인 사용에 권장",
                                 u"Recomendado para el uso diario"),
"Test versions":     (u"Testversionen", u"Versions de test", u"테스트 버전", u"Versiones de prueba"),
"Preview versions for trying upcoming changes":
    (u"Vorabversionen, um kommende Änderungen auszuprobieren",
     u"Versions d'essai pour tester les changements à venir",
     u"다가올 변경 사항을 미리 시험해 보는 버전",
     u"Versiones previas para probar los próximos cambios"),
"Experimental versions (nightly)": (u"Experimentelle Versionen (Nightly)",
                                    u"Versions expérimentales (nightly)",
                                    u"실험 버전 (나이트리)",
                                    u"Versiones experimentales (nightly)"),
"The newest changes, least tested": (u"Die neuesten Änderungen, am wenigsten getestet",
                                     u"Les changements les plus récents, les moins testés",
                                     u"가장 새로운 변경, 가장 적게 검증됨",
                                     u"Los cambios más nuevos, los menos probados"),

# ---- count pill -----------------------------------------------------------
"version":           (u"Version", u"version", u"버전", u"versión"),
"versions":          (u"Versionen", u"versions", u"버전", u"versiones"),

# ---- badges ---------------------------------------------------------------
"Newer than installed":   (u"Neuer als installiert", u"Plus récente qu'installée",
                           u"설치된 것보다 최신", u"Más nueva que la instalada"),
"Older than installed":   (u"Älter als installiert", u"Plus ancienne qu'installée",
                           u"설치된 것보다 이전", u"Más antigua que la instalada"),
"Currently installed":    (u"Derzeit installiert", u"Actuellement installée",
                           u"현재 설치됨", u"Instalada ahora"),
"Blocked":                (u"Gesperrt", u"Bloquée", u"차단됨", u"Bloqueada"),
"Not supported on this device": (u"Auf diesem Gerät nicht unterstützt",
                                 u"Non pris en charge sur cet appareil",
                                 u"이 장치에서는 지원되지 않음",
                                 u"No compatible con este dispositivo"),

# ---- confirm screen -------------------------------------------------------
"Install this version?":  (u"Diese Version installieren?", u"Installer cette version ?",
                           u"이 버전을 설치할까요?", u"¿Instalar esta versión?"),
"This version can't be installed": (u"Diese Version lässt sich nicht installieren",
                                    u"Cette version ne peut pas être installée",
                                    u"이 버전은 설치할 수 없습니다",
                                    u"Esta versión no se puede instalar"),
"Downgrade":         (u"Ältere Version", u"Rétrogradation", u"이전 버전", u"Bajar versión"),
"This installs an OLDER version than the one installed.":
    (u"Das installiert eine ÄLTERE Version als die installierte.",
     u"Ceci installe une version PLUS ANCIENNE que celle installée.",
     u"설치된 것보다 이전 버전을 설치합니다.",
     u"Esto instala una versión MÁS ANTIGUA que la instalada."),
"Loading…":          (u"Lädt…", u"Chargement…", u"불러오는 중…", u"Cargando…"),

# ---- install run ----------------------------------------------------------
"Checking prerequisites…": (u"Voraussetzungen werden geprüft…", u"Vérification des prérequis…",
                            u"필수 요소 확인 중…", u"Comprobando requisitos…"),
"First install on this PC — Windows will ask for permission once.":
    (u"Erste Installation auf diesem PC — Windows fragt einmal nach der Erlaubnis.",
     u"Première installation sur ce PC — Windows demandera l'autorisation une fois.",
     u"이 PC의 첫 설치입니다 — Windows가 한 번 권한을 묻습니다.",
     u"Primera instalación en este PC — Windows pedirá permiso una vez."),
"Opening Game Bar — the ClawTweaks widget will start the helper…":
    (u"Game Bar wird geöffnet — das ClawTweaks-Widget startet den Helper…",
     u"Ouverture de la Game Bar — le widget ClawTweaks démarre le helper…",
     u"Game Bar를 여는 중 — ClawTweaks 위젯이 헬퍼를 시작합니다…",
     u"Abriendo la Game Bar — el widget de ClawTweaks arrancará el helper…"),

# ---- footer chip: "Install this build" was the old wording ----------------
"Install this version":   (u"Diese Version installieren", u"Installer cette version",
                           u"이 버전 설치", u"Instalar esta versión"),
}
