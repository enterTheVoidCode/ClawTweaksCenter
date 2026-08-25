# -*- coding: utf-8 -*-
import io, unicodedata, sys

def width(s):
    return sum(2 if unicodedata.east_asian_width(c) in ('W', 'F') else 1 for c in s)

def budget(en):
    return max(width(en) + 5, int(width(en) * 1.7))

# english : (de, fr, ko, es)
T = {
# ---- footer action chips -------------------------------------------------
"Back":              (u"Zurück", u"Retour", u"뒤로", u"Atrás"),
"Back to library":   (u"Zur Bibliothek", u"Retour bibliothèque", u"라이브러리로", u"A la biblioteca"),
"Cancel":            (u"Abbrechen", u"Annuler", u"취소", u"Cancelar"),
"Choose":            (u"Wählen", u"Choisir", u"선택", u"Elegir"),
"Close":             (u"Schließen", u"Fermer", u"닫기", u"Cerrar"),
"Close Center":      (u"Center beenden", u"Fermer Center", u"Center 종료", u"Cerrar Center"),
"Exit":              (u"Beenden", u"Quitter", u"종료", u"Salir"),
"Info":              (u"Info", u"Infos", u"정보", u"Info"),
"Menu":              (u"Menü", u"Menu", u"메뉴", u"Menú"),
"Open":              (u"Öffnen", u"Ouvrir", u"열기", u"Abrir"),
"Play":              (u"Spielen", u"Jouer", u"실행", u"Jugar"),
"Refresh":           (u"Neu laden", u"Actualiser", u"새로 고침", u"Actualizar"),
"Refresh status":    (u"Status neu laden", u"Actualiser l'état", u"상태 새로 고침", u"Actualizar estado"),
"Rescan":            (u"Neu suchen", u"Rescanner", u"다시 검색", u"Reescanear"),
"Run":               (u"Starten", u"Lancer", u"실행", u"Ejecutar"),
"Save":              (u"Speichern", u"Enregistrer", u"저장", u"Guardar"),
"Search":            (u"Suchen", u"Rechercher", u"검색", u"Buscar"),
"Select":            (u"Auswählen", u"Sélectionner", u"선택", u"Seleccionar"),
"Settings":          (u"Einstellungen", u"Paramètres", u"설정", u"Ajustes"),
"Add app":           (u"Hinzufügen", u"Ajouter", u"앱 추가", u"Añadir app"),
"Edit name":         (u"Name ändern", u"Modifier le nom", u"이름 편집", u"Editar nombre"),
"Edit search":       (u"Suche ändern", u"Modifier", u"검색 편집", u"Editar búsqueda"),
"Set as cover":      (u"Als Cover setzen", u"Définir en cover", u"커버로 설정", u"Usar de portada"),
"Create backup":     (u"Backup erstellen", u"Créer un backup", u"백업 생성", u"Crear backup"),
"Install this build": (u"Diesen Build installieren", u"Installer ce build", u"이 빌드 설치", u"Instalar este build"),
"Open download page": (u"Downloadseite öffnen", u"Page de téléchargement", u"다운로드 페이지 열기", u"Abrir página de descarga"),
"Open SteamGridDB":  (u"SteamGridDB öffnen", u"Ouvrir SteamGridDB", u"SteamGridDB 열기", u"Abrir SteamGridDB"),
"Open AnyFSE":       (u"AnyFSE öffnen", u"Ouvrir AnyFSE", u"AnyFSE 열기", u"Abrir AnyFSE"),
"Copy Center path":  (u"Center-Pfad kopieren", u"Copier le chemin", u"Center 경로 복사", u"Copiar ruta de Center"),
"Yes, install":      (u"Ja, installieren", u"Oui, installer", u"예, 설치", u"Sí, instalar"),
"Yes, restore":      (u"Ja, wiederherstellen", u"Oui, restaurer", u"예, 복원", u"Sí, restaurar"),
"Yes, reset everything": (u"Ja, alles zurücksetzen", u"Oui, tout réinitialiser", u"예, 모두 초기화", u"Sí, restablecer todo"),

"Game Library":      (u"Bibliothek", u"Bibliothèque", u"라이브러리", u"Biblioteca"),
"Re-check":          (u"Neu prüfen", u"Revérifier", u"다시 확인", u"Recomprobar"),

# ---- tabs ----------------------------------------------------------------
"Start":     (u"Start", u"Accueil", u"시작", u"Inicio"),
"Library":   (u"Bibliothek", u"Bibliothèque", u"라이브러리", u"Biblioteca"),
"Recent":    (u"Zuletzt", u"Récents", u"최근", u"Recientes"),
"Favorites": (u"Favoriten", u"Favoris", u"즐겨찾기", u"Favoritos"),
"All":       (u"Alle", u"Tous", u"전체", u"Todos"),
"Misc":      (u"Sonstige", u"Divers", u"기타", u"Otros"),
"Platform":  (u"Plattform", u"Plateforme", u"플랫폼", u"Plataforma"),
"System":    (u"System", u"Système", u"시스템", u"Sistema"),

# ---- home tile descriptions ---------------------------------------------
"Install releases, test builds and nightlies.":
    (u"Releases, Test-Builds und Nightlies installieren.",
     u"Installer releases, test builds et nightlies.",
     u"릴리즈, 테스트 빌드, 나이트리를 설치합니다.",
     u"Instala releases, test builds y nightlies."),
"Set up Center M, controller and Game Bar.":
    (u"Center M, Controller und Game Bar einrichten.",
     u"Configurer Center M, la manette et Game Bar.",
     u"Center M, 컨트롤러, Game Bar를 설정합니다.",
     u"Configura Center M, el mando y Game Bar."),
"Reset the app, or back up your profiles.":
    (u"App zurücksetzen oder Profile sichern.",
     u"Réinitialiser l'app ou sauvegarder vos profils.",
     u"앱을 초기화하거나 프로필을 백업합니다.",
     u"Restablece la app o guarda tus perfiles."),
"Play your Steam, Epic and Xbox games.":
    (u"Steam-, Epic- und Xbox-Spiele spielen.",
     u"Jouez à vos jeux Steam, Epic et Xbox.",
     u"Steam, Epic, Xbox 게임을 실행합니다.",
     u"Juega a tus juegos de Steam, Epic y Xbox."),
"Choose how the library starts and looks.":
    (u"Start und Aussehen der Bibliothek wählen.",
     u"Choisir l'ouverture et l'aspect de la bibliothèque.",
     u"라이브러리의 시작과 모양을 선택합니다.",
     u"Elige cómo se abre y se ve la biblioteca."),
"Choose the language and how the window opens.":
    (u"Sprache und Fenstermodus wählen.",
     u"Choisir la langue et le mode de fenêtre.",
     u"언어와 창 모드를 선택합니다.",
     u"Elige el idioma y el modo de ventana."),

# ---- settings screens ----------------------------------------------------
"Library settings":  (u"Bibliothek Einstellungen", u"Réglages bibliothèque", u"라이브러리 설정", u"Ajustes de biblioteca"),
"Center settings":   (u"Center-Einstellungen", u"Paramètres Center", u"Center 설정", u"Ajustes de Center"),
"Language":          (u"Sprache", u"Langue", u"언어", u"Idioma"),
"System language":   (u"Systemsprache", u"Langue du système", u"시스템 언어", u"Idioma del sistema"),
"Fullscreen":        (u"Vollbild", u"Plein écran", u"전체 화면", u"Pantalla completa"),
"Start in the library": (u"In der Bibliothek starten", u"Démarrer dans la bibliothèque",
                         u"라이브러리로 시작", u"Empezar en la biblioteca"),
"Square ROM art":    (u"Quadratische ROM-Cover", u"Jaquettes ROM carrées", u"정사각형 ROM 아트", u"Portadas ROM cuadradas"),
"Immersive mode":    (u"Immersiver Modus", u"Mode immersif", u"몰입 모드", u"Modo inmersivo"),
"After starting a game": (u"Nach dem Spielstart", u"Après le lancement", u"게임 실행 후", u"Tras iniciar un juego"),
"Start Center with ClawTweaks": (u"Center mit ClawTweaks starten", u"Démarrer Center avec ClawTweaks",
                                 u"ClawTweaks와 함께 시작", u"Iniciar Center con ClawTweaks"),
"Run in background": (u"Im Hintergrund laufen", u"Exécuter en arrière-plan", u"백그라운드 실행", u"Ejecutar en segundo plano"),
"SteamGridDB key":   (u"SteamGridDB-Key", u"Clé SteamGridDB", u"SteamGridDB 키", u"Clave de SteamGridDB"),
"Minimize":          (u"Minimieren", u"Réduire", u"최소화", u"Minimizar"),
"Stay open":         (u"Offen lassen", u"Rester ouvert", u"열어 두기", u"Dejar abierto"),
"Not set.":          (u"Kein Key.", u"Non défini.", u"설정 안 됨.", u"Sin definir."),
"Set. Covers are downloaded for games with none.":
    (u"Gesetzt. Fehlende Cover werden geladen.",
     u"Défini. Les jaquettes manquantes sont téléchargées.",
     u"설정됨. 없는 커버를 내려받습니다.",
     u"Definida. Se descargan las portadas que faltan."),
"Click the right stick to show the button hints":
    (u"Rechten Stick drücken, um die Tastenhinweise zu zeigen",
     u"Cliquez le stick droit pour afficher les boutons",
     u"오른쪽 스틱을 누르면 버튼 안내가 표시됩니다",
     u"Pulsa el stick derecho para ver los botones"),

# ---- header chip ---------------------------------------------------------
"Checking…":    (u"Prüfe…", u"Vérification…", u"확인 중…", u"Comprobando…"),
"ClawTweaks not installed": (u"ClawTweaks nicht installiert", u"ClawTweaks non installé",
                             u"ClawTweaks 미설치", u"ClawTweaks no instalado"),
}

LANGS = ["German", "French", "Korean", "Spanish"]
kept = dict((l, []) for l in LANGS)
dropped = []
for en in sorted(T):
    tr = T[en]
    b = budget(en)
    for idx, lang in enumerate(LANGS):
        t = tr[idx]
        if width(t) > b:
            dropped.append((lang, en, t, width(t), b))
        else:
            kept[lang].append((en, t))

def esc(s):
    out = []
    for c in s:
        if c == '"':
            out.append('\\"')
        elif c == '\\':
            out.append('\\\\')
        elif ord(c) < 128:
            out.append(c)
        else:
            out.append('\\u%04X' % ord(c))
    return ''.join(out)

head = '''using System.Collections.Generic;

namespace ClawTweaksCenter.Core
{
    public static partial class Loc
    {
        // The tables behind T(). Keyed by the English string; anything absent renders in English,
        // which is what makes leaving a string out a decision rather than a bug.
        //
        // WIDTH-CHECKED. Every entry passed a rendered-width check against its English original: at
        // most 1.7x the English, or five characters more, whichever is larger, with CJK characters
        // counted double because they render about twice as wide. Center's chips, tabs and tiles are
        // sized for the English word and do not grow, so a label that fails the check is left out
        // and stays English rather than being clipped. The ones that failed are listed at the bottom
        // of this file - that list is the record of what is deliberately NOT translated.
        //
        // Menu headings are English on purpose: the Home tiles keep their English titles and only
        // their one-line descriptions are translated. "Library" is the exception, and it is
        // translated everywhere it appears.

'''
body = []
for lang in LANGS:
    body.append("        private static readonly Dictionary<string, string> %s = new Dictionary<string, string>\r\n        {\r\n" % lang)
    for en, t in kept[lang]:
        body.append('            ["%s"] = "%s",\r\n' % (esc(en), esc(t)))
    body.append("        };\r\n\r\n")

tail = "    }\r\n}\r\n"

note = []
if dropped:
    note.append("\r\n/*\r\n * LEFT IN ENGLISH ON PURPOSE - the honest translation is wider than the control it has to\r\n")
    note.append(" * fit in (see the width rule above). This list is the answer to \"why is this one word\r\n")
    note.append(" * still English\", so it is kept rather than tidied away:\r\n *\r\n")
    for lang, en, t, w, b in sorted(dropped):
        note.append(' *   %-8s "%s" -> "%s" (%d wide, budget %d)\r\n' % (lang + ":", en, t, w, b))
    note.append(" */\r\n")

src = head + ''.join(body) + tail + (''.join(note) if dropped else '')
io.open('ClawTweaksCenter/Core/Localization.Tables.cs', 'w', encoding='utf-8-sig', newline='').write(src)

sys.stdout.write("entries per language: %s\n" % str(dict((l, len(kept[l])) for l in LANGS)))
sys.stdout.write("dropped: %d\n" % len(dropped))
for d in sorted(dropped):
    sys.stdout.write("   %-7s %-48s w=%d budget=%d\n" % (d[0], d[1][:48], d[3], d[4]))
