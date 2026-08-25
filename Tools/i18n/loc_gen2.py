# -*- coding: utf-8 -*-
"""Round two: everything that reaches a translated builder (see survey4.py).

Date FORMAT strings ("d MMM yyyy") deliberately absent - they reach a builder by accident and
translating one would corrupt the date.
"""
import io, os, sys, unicodedata
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import loc_gen                                        # regenerates round one as a side effect
from loc_onb import ONBOARDING                        # round three: the onboarding step cards
from loc_inst import INSTALLER                        # round four: installer + maintenance prose
from loc_pad import PAD                               # round five: the pad-button sentences
from loc_gaps import GAPS                             # round six: gaps reported from the device
from loc_setup import SETUP                           # round seven: the Center installer window
from loc_browse import BROWSE                         # round eight: the Update & Release screen

T = dict(loc_gen.T)

# english : (de, fr, ko, es)
T.update({

# ---- game menu -----------------------------------------------------------
"Add to Favorites":      (u"Zu Favoriten", u"Aux favoris", u"즐겨찾기 추가", u"A favoritos"),
"Remove from Favorites": (u"Aus Favoriten entfernen", u"Retirer des favoris", u"즐겨찾기 해제", u"Quitar de favoritos"),
"Choose cover art…":     (u"Cover wählen…", u"Choisir une jaquette…", u"커버 선택…", u"Elegir portada…"),
"Choose cover art":      (u"Cover wählen", u"Choisir une jaquette", u"커버 선택", u"Elegir portada"),
"Choose cover":          (u"Cover wählen", u"Choisir", u"커버 선택", u"Elegir portada"),
"Search SteamGridDB for a different cover":
    (u"Auf SteamGridDB ein anderes Cover suchen",
     u"Chercher une autre jaquette sur SteamGridDB",
     u"SteamGridDB에서 다른 커버 찾기",
     u"Busca otra portada en SteamGridDB"),
"Set a SteamGridDB key in Settings first":
    (u"Erst einen SteamGridDB-Key in den Einstellungen setzen",
     u"Définissez d'abord une clé SteamGridDB",
     u"먼저 설정에서 SteamGridDB 키를 입력하세요",
     u"Define antes una clave de SteamGridDB"),
"Also looks for new cover art": (u"Sucht auch nach neuem Cover", u"Cherche aussi une jaquette",
                                 u"새 커버도 함께 찾습니다", u"También busca nueva portada"),
"Remove from library":   (u"Aus Bibliothek entfernen", u"Retirer de la bibliothèque",
                          u"라이브러리에서 제거", u"Quitar de la biblioteca"),
"Deletes the entry, not the app": (u"Löscht den Eintrag, nicht die App", u"Supprime l'entrée, pas l'app",
                                   u"앱이 아니라 항목만 삭제", u"Borra la entrada, no la app"),
"Setting cover…":        (u"Cover wird gesetzt…", u"Application…", u"커버 적용 중…", u"Aplicando portada…"),
"No portrait covers found for that search.":
    (u"Keine Hochkant-Cover für diese Suche gefunden.",
     u"Aucune jaquette portrait pour cette recherche.",
     u"해당 검색에 세로 커버가 없습니다.",
     u"No hay portadas verticales para esa búsqueda."),
"Portrait covers only. No match? Edit the text above and search again.":
    (u"Nur Hochkant-Cover. Nichts dabei? Text oben ändern und neu suchen.",
     u"Jaquettes portrait uniquement. Rien ? Modifiez le texte et relancez.",
     u"세로 커버만 표시됩니다. 없으면 위 문구를 고쳐 다시 검색하세요.",
     u"Solo portadas verticales. ¿Nada? Cambia el texto y busca otra vez."),
"Renaming looks for new cover art.":
    (u"Beim Umbenennen wird neues Cover gesucht.",
     u"Renommer relance la recherche de jaquette.",
     u"이름을 바꾸면 커버를 다시 찾습니다.",
     u"Al renombrar se busca nueva portada."),

# ---- library: states, exit menu, info ------------------------------------
"Reading your stores…":  (u"Deine Stores werden gelesen…", u"Lecture de vos stores…",
                          u"스토어를 읽는 중…", u"Leyendo tus tiendas…"),
"Preparing your library…": (u"Bibliothek wird vorbereitet…", u"Préparation de la bibliothèque…",
                            u"라이브러리 준비 중…", u"Preparando la biblioteca…"),
"Detecting device…":     (u"Gerät wird erkannt…", u"Détection de l'appareil…",
                          u"장치 감지 중…", u"Detectando el dispositivo…"),
"Last played":           (u"Zuletzt gespielt", u"Dernière partie", u"마지막 플레이", u"Última partida"),
"Nothing found yet.":    (u"Noch nichts gefunden.", u"Rien trouvé pour l'instant.",
                          u"아직 찾은 것이 없습니다.", u"Aún no hay nada."),
"No game has been played yet.": (u"Noch kein Spiel gespielt.", u"Aucun jeu joué pour l'instant.",
                                 u"아직 플레이한 게임이 없습니다.", u"Aún no has jugado a nada."),
"No Epic games installed.": (u"Keine Epic-Spiele installiert.", u"Aucun jeu Epic installé.",
                             u"설치된 Epic 게임이 없습니다.", u"No hay juegos de Epic."),
"No Steam games installed.": (u"Keine Steam-Spiele installiert.", u"Aucun jeu Steam installé.",
                              u"설치된 Steam 게임이 없습니다.", u"No hay juegos de Steam."),
"No Xbox games installed.": (u"Keine Xbox-Spiele installiert.", u"Aucun jeu Xbox installé.",
                             u"설치된 Xbox 게임이 없습니다.", u"No hay juegos de Xbox."),
"No tools added yet.":   (u"Noch keine Tools hinzugefügt.", u"Aucun outil ajouté.",
                          u"추가된 도구가 없습니다.", u"No hay herramientas."),
"No favorites yet.":     (u"Noch keine Favoriten.", u"Aucun favori.",
                          u"즐겨찾기가 없습니다.", u"No hay favoritos."),
"Playnite is not installed.": (u"Playnite ist nicht installiert.", u"Playnite n'est pas installé.",
                               u"Playnite가 설치되지 않았습니다.", u"Playnite no está instalado."),
"No ROM has been played yet.": (u"Noch kein ROM gespielt.", u"Aucune ROM jouée.",
                                u"플레이한 ROM이 없습니다.", u"Aún no has jugado ROMs."),
"No ROMs in your Playnite library.": (u"Keine ROMs in deiner Playnite-Bibliothek.",
                                      u"Aucune ROM dans votre bibliothèque Playnite.",
                                      u"Playnite 라이브러리에 ROM이 없습니다.",
                                      u"No hay ROMs en tu biblioteca de Playnite."),
"No games found.":       (u"Keine Spiele gefunden.", u"Aucun jeu trouvé.",
                          u"게임을 찾지 못했습니다.", u"No se encontraron juegos."),
"All systems":           (u"Alle Systeme", u"Tous les systèmes", u"모든 시스템", u"Todos los sistemas"),
"Right stick":           (u"Rechter Stick", u"Stick droit", u"오른쪽 스틱", u"Stick derecho"),
"Leave the library":     (u"Bibliothek verlassen", u"Quitter la bibliothèque",
                          u"라이브러리 나가기", u"Salir de la biblioteca"),
"Minimize to tray":      (u"In die Taskleiste", u"Réduire dans la zone",
                          u"트레이로 최소화", u"Minimizar a la bandeja"),
"Center keeps running.": (u"Center läuft weiter.", u"Center continue de tourner.",
                          u"Center가 계속 실행됩니다.", u"Center sigue en marcha."),
"Center start screen":   (u"Center-Startseite", u"Écran d'accueil Center",
                          u"Center 시작 화면", u"Pantalla de inicio"),
"Leave the library open.": (u"Bibliothek offen lassen.", u"Laisser la bibliothèque ouverte.",
                            u"라이브러리를 열어 둡니다.", u"Deja la biblioteca abierta."),
"Ends Center completely.": (u"Beendet Center vollständig.", u"Ferme Center complètement.",
                            u"Center를 완전히 종료합니다.", u"Cierra Center por completo."),
"Minimize Center":       (u"Center minimieren", u"Réduire Center", u"Center 최소화", u"Minimizar Center"),
"Your ClawTweaks library": (u"Deine ClawTweaks-Bibliothek", u"Votre bibliothèque ClawTweaks",
                            u"내 ClawTweaks 라이브러리", u"Tu biblioteca de ClawTweaks"),
"Turn on Settings → Start in the library to open here every time.":
    (u"Schalte Einstellungen → In der Bibliothek starten ein, um immer hier zu landen.",
     u"Activez Paramètres → Démarrer dans la bibliothèque pour ouvrir ici à chaque fois.",
     u"설정 → 라이브러리로 시작을 켜면 항상 여기서 열립니다.",
     u"Activa Ajustes → Empezar en la biblioteca para abrir siempre aquí."),
"Your games":            (u"Deine Spiele", u"Vos jeux", u"내 게임", u"Tus juegos"),
"Shows your installed Steam, Xbox and Epic games.":
    (u"Zeigt deine installierten Steam-, Xbox- und Epic-Spiele.",
     u"Affiche vos jeux Steam, Xbox et Epic installés.",
     u"설치된 Steam, Xbox, Epic 게임을 보여줍니다.",
     u"Muestra tus juegos instalados de Steam, Xbox y Epic."),
"Steam cover art is found automatically.":
    (u"Steam-Cover werden automatisch gefunden.",
     u"Les jaquettes Steam sont trouvées automatiquement.",
     u"Steam 커버는 자동으로 찾습니다.",
     u"Las portadas de Steam se buscan solas."),
"Add ROMs in Playnite — they are imported from there.":
    (u"ROMs in Playnite anlegen — von dort werden sie importiert.",
     u"Ajoutez vos ROMs dans Playnite — elles sont importées de là.",
     u"ROM은 Playnite에 추가하면 가져옵니다.",
     u"Añade ROMs en Playnite — se importan desde ahí."),
"Covers for Xbox, Epic and your own games":
    (u"Cover für Xbox, Epic und eigene Spiele",
     u"Jaquettes pour Xbox, Epic et vos jeux",
     u"Xbox, Epic, 직접 추가한 게임의 커버",
     u"Portadas para Xbox, Epic y tus juegos"),
"Create a free SteamGridDB account.":
    (u"Kostenloses SteamGridDB-Konto anlegen.",
     u"Créez un compte SteamGridDB gratuit.",
     u"무료 SteamGridDB 계정을 만드세요.",
     u"Crea una cuenta gratis en SteamGridDB."),
"Copy your key from Preferences → API Key.":
    (u"Key unter Preferences → API Key kopieren.",
     u"Copiez la clé dans Preferences → API Key.",
     u"Preferences → API Key에서 키를 복사하세요.",
     u"Copia la clave en Preferences → API Key."),
"Add it under Settings → SteamGridDB key.":
    (u"Unter Einstellungen → SteamGridDB-Key eintragen.",
     u"Ajoutez-la dans Paramètres → clé SteamGridDB.",
     u"설정 → SteamGridDB 키에 입력하세요.",
     u"Añádela en Ajustes → clave de SteamGridDB."),
"Turn it on under Settings to show covers only.":
    (u"In den Einstellungen einschalten, um nur Cover zu zeigen.",
     u"Activez-le dans Paramètres pour n'afficher que les jaquettes.",
     u"설정에서 켜면 커버만 표시됩니다.",
     u"Actívalo en Ajustes para ver solo portadas."),
"Full screen with AnyFSE": (u"Vollbild mit AnyFSE", u"Plein écran avec AnyFSE",
                            u"AnyFSE로 전체 화면", u"Pantalla completa con AnyFSE"),
"Add ClawTweaks Center in AnyFSE as your full screen app.":
    (u"ClawTweaks Center in AnyFSE als Vollbild-App eintragen.",
     u"Ajoutez ClawTweaks Center comme app plein écran dans AnyFSE.",
     u"AnyFSE에서 ClawTweaks Center를 전체 화면 앱으로 등록하세요.",
     u"Añade ClawTweaks Center como app a pantalla completa en AnyFSE."),
"Enter the path below, then turn on Start in the library.":
    (u"Den Pfad unten eintragen, dann In der Bibliothek starten einschalten.",
     u"Saisissez le chemin ci-dessous, puis activez Démarrer dans la bibliothèque.",
     u"아래 경로를 입력한 뒤 라이브러리로 시작을 켜세요.",
     u"Escribe la ruta de abajo y activa Empezar en la biblioteca."),
"About this library":    (u"Über diese Bibliothek", u"À propos de cette bibliothèque",
                          u"이 라이브러리 정보", u"Sobre esta biblioteca"),
"That key was rejected.": (u"Der Key wurde abgelehnt.", u"Cette clé a été refusée.",
                           u"키가 거부되었습니다.", u"Esa clave fue rechazada."),
"Center comes back when the game ends.":
    (u"Center kommt zurück, wenn das Spiel endet.",
     u"Center revient à la fin du jeu.",
     u"게임이 끝나면 Center로 돌아옵니다.",
     u"Center vuelve al terminar el juego."),

# ---- misc overlay --------------------------------------------------------
"Add a tool":            (u"Tool hinzufügen", u"Ajouter un outil", u"도구 추가", u"Nueva herramienta"),
"Choose from installed apps": (u"Aus installierten Apps wählen", u"Choisir parmi les apps installées",
                               u"설치된 앱에서 선택", u"Elegir de las apps instaladas"),
"Start menu, desktop and startup": (u"Startmenü, Desktop und Autostart",
                                    u"Menu Démarrer, bureau et démarrage",
                                    u"시작 메뉴, 바탕 화면, 시작 프로그램",
                                    u"Menú Inicio, escritorio y arranque"),
"Browse for a file":     (u"Datei auswählen", u"Parcourir un fichier", u"파일 찾아보기", u"Buscar un archivo"),
"Windows file picker":   (u"Windows-Dateiauswahl", u"Sélecteur de fichiers Windows",
                          u"Windows 파일 선택기", u"Selector de archivos"),
"Choose what to add":    (u"Wählen, was hinzukommt", u"Choisir quoi ajouter",
                          u"추가할 항목 선택", u"Elige qué añadir"),
"This takes a moment.":  (u"Das dauert einen Moment.", u"Cela prend un instant.",
                          u"잠시 걸립니다.", u"Esto tarda un momento."),
"Nothing found to add.": (u"Nichts zum Hinzufügen gefunden.", u"Rien à ajouter.",
                          u"추가할 항목이 없습니다.", u"No hay nada que añadir."),
"Reading installed apps…": (u"Installierte Apps werden gelesen…", u"Lecture des apps installées…",
                            u"설치된 앱을 읽는 중…", u"Leyendo apps instaladas…"),

# ---- maintenance ---------------------------------------------------------
"Manage your ClawTweaks settings — back them up, restore a previous backup, or reset everything to a clean state.":
    (u"Verwalte deine ClawTweaks-Einstellungen — sichern, ein Backup zurückholen oder alles zurücksetzen.",
     u"Gérez vos réglages ClawTweaks — sauvegarde, restauration, ou remise à zéro complète.",
     u"ClawTweaks 설정을 관리합니다 — 백업, 복원, 또는 전체 초기화.",
     u"Gestiona tus ajustes de ClawTweaks — copia, restaura o restablece todo."),
"ClawTweaks is not installed.": (u"ClawTweaks ist nicht installiert.", u"ClawTweaks n'est pas installé.",
                                 u"ClawTweaks가 설치되지 않았습니다.", u"ClawTweaks no está instalado."),
"CTW Full Reset":        (u"CTW ganz zurücksetzen", u"Reset complet CTW",
                          u"CTW 전체 초기화", u"Restablecer todo CTW"),
"Create Backup":         (u"Backup erstellen", u"Créer un backup", u"백업 생성", u"Crear copia"),
"Restore Backup":        (u"Backup zurückspielen", u"Restaurer un backup", u"백업 복원", u"Restaurar copia"),
"This resets ALL ClawTweaks settings to a clean state:":
    (u"Das setzt ALLE ClawTweaks-Einstellungen zurück:",
     u"Cela remet TOUS les réglages ClawTweaks à zéro :",
     u"모든 ClawTweaks 설정을 초기 상태로 되돌립니다:",
     u"Esto restablece TODOS los ajustes de ClawTweaks:"),
"A safety backup is saved first": (u"Vorher wird ein Sicherungs-Backup angelegt",
                                   u"Une sauvegarde de sécurité est faite d'abord",
                                   u"먼저 안전 백업을 저장합니다",
                                   u"Antes se guarda una copia de seguridad"),
"No backups found":      (u"Keine Backups gefunden", u"Aucun backup trouvé",
                          u"백업을 찾지 못했습니다", u"No hay copias"),
"Restore this backup?":  (u"Dieses Backup zurückholen?", u"Restaurer ce backup ?",
                          u"이 백업을 복원할까요?", u"¿Restaurar esta copia?"),
"No backup selected":    (u"Kein Backup ausgewählt", u"Aucun backup sélectionné",
                          u"선택된 백업 없음", u"Sin copia seleccionada"),
"What happens":          (u"Was passiert", u"Ce qui se passe", u"무엇이 일어나는지", u"Qué ocurre"),
"Reset complete":        (u"Reset fertig", u"Reset terminé",
                          u"초기화 완료", u"Restablecimiento listo"),
"Reset failed":          (u"Reset fehlgeschlagen", u"Échec du reset",
                          u"초기화 실패", u"Fallo al restablecer"),
"Backup created":        (u"Backup erstellt", u"Backup créé", u"백업 생성됨", u"Copia creada"),
"Backup failed":         (u"Backup fehlgeschlagen", u"Échec du backup", u"백업 실패", u"Fallo de copia"),
"Restore complete":      (u"Zurückspielen fertig", u"Restauration terminée",
                          u"복원 완료", u"Restauración lista"),
"Restore failed":        (u"Nicht zurückgespielt", u"Restauration échouée",
                          u"복원 실패", u"Fallo al restaurar"),
"Different device":      (u"Anderes Gerät", u"Autre appareil", u"다른 장치", u"Otro dispositivo"),
"Not a recognized backup": (u"Kein erkanntes Backup", u"Backup non reconnu",
                            u"인식되지 않는 백업", u"Copia no reconocida"),
"Resetting all ClawTweaks settings…": (u"Alle ClawTweaks-Einstellungen werden zurückgesetzt…",
                                       u"Réinitialisation de tous les réglages…",
                                       u"모든 ClawTweaks 설정을 초기화하는 중…",
                                       u"Restableciendo todos los ajustes…"),
"Creating backup…":      (u"Backup wird erstellt…", u"Création du backup…",
                          u"백업 생성 중…", u"Creando copia…"),

# ---- home / shell --------------------------------------------------------
"ClawTweaks installed":  (u"ClawTweaks installiert", u"ClawTweaks installé",
                          u"ClawTweaks 설치됨", u"ClawTweaks instalado"),
"ClawTweaks updated":    (u"ClawTweaks aktualisiert", u"ClawTweaks mis à jour",
                          u"ClawTweaks 업데이트됨", u"ClawTweaks actualizado"),
"Choose what to do next.": (u"Wähle, wie es weitergeht.", u"Choisissez la suite.",
                            u"다음에 무엇을 할지 선택하세요.", u"Elige cómo seguir."),
"Finish here and close the window.": (u"Hier fertig, Fenster schließen.",
                                      u"Terminer ici et fermer la fenêtre.",
                                      u"여기서 끝내고 창을 닫습니다.",
                                      u"Termina aquí y cierra la ventana."),
"This Setup build is outdated": (u"Dieser Setup-Build ist veraltet", u"Ce build de Setup est obsolète",
                                 u"이 Setup 빌드는 오래되었습니다", u"Este build de Setup es antiguo"),
"Windows Insider Preview detected": (u"Windows Insider Preview erkannt",
                                     u"Windows Insider Preview détecté",
                                     u"Windows Insider Preview 감지됨",
                                     u"Windows Insider Preview detectado"),
"Helps set the most important ClawTweaks settings.":
    (u"Hilft bei den wichtigsten ClawTweaks-Einstellungen.",
     u"Aide à régler l'essentiel de ClawTweaks.",
     u"가장 중요한 ClawTweaks 설정을 도와줍니다.",
     u"Ayuda con los ajustes clave de ClawTweaks."),
"Couldn't load":         (u"Konnte nicht laden", u"Chargement impossible",
                          u"불러오지 못했습니다", u"No se pudo cargar"),
"Nothing found":         (u"Nichts gefunden", u"Rien trouvé", u"찾지 못했습니다", u"Nada encontrado"),
"To tray":               (u"Taskleiste", u"Réduire", u"트레이로", u"A la bandeja"),
"This build can't be installed": (u"Dieser Build lässt sich nicht installieren",
                                  u"Ce build ne peut pas être installé",
                                  u"이 빌드는 설치할 수 없습니다",
                                  u"Este build no se puede instalar"),
"Desktop icon":          (u"Desktop-Symbol", u"Icône bureau", u"바탕 화면 아이콘", u"Icono de escritorio"),

# ---- installer / phases --------------------------------------------------
"Controller health":     (u"Controller-Zustand", u"État de la manette",
                          u"컨트롤러 상태", u"Estado del mando"),
"Probing controller topology": (u"Controller-Topologie wird geprüft", u"Analyse de la manette",
                                u"컨트롤러 구성을 확인하는 중", u"Analizando el mando"),
"Physical MSI Claw controller": (u"Physischer MSI-Claw-Controller", u"Manette MSI Claw physique",
                                 u"물리 MSI Claw 컨트롤러", u"Mando físico MSI Claw"),
"Virtual controller (VIIPER)": (u"Virtueller Controller (VIIPER)", u"Manette virtuelle (VIIPER)",
                                u"가상 컨트롤러 (VIIPER)", u"Mando virtual (VIIPER)"),
"XInput controllers":    (u"XInput-Controller", u"Manettes XInput", u"XInput 컨트롤러", u"Mandos XInput"),
"Steam Xbox filter driver": (u"Steam-Xbox-Filtertreiber", u"Pilote de filtre Steam Xbox",
                             u"Steam Xbox 필터 드라이버", u"Filtro Xbox de Steam"),
"Not running (good).":   (u"Läuft nicht (gut).", u"Ne tourne pas (bien).",
                          u"실행 중 아님 (양호).", u"No está activo (bien)."),
"Not present (good).":   (u"Nicht vorhanden (gut).", u"Absent (bien).",
                          u"없음 (양호).", u"No presente (bien)."),
"Final controller check": (u"Letzte Controller-Prüfung", u"Vérification finale de la manette",
                           u"마지막 컨트롤러 확인", u"Comprobación final del mando"),
"Almost done":           (u"Fast fertig", u"Presque fini", u"거의 다 됐습니다", u"Casi listo"),
"Mode: Update":          (u"Modus: Update", u"Mode : mise à jour", u"모드: 업데이트", u"Modo: actualizar"),
"Mode: Fresh install":   (u"Modus: Neuinstallation", u"Mode : installation neuve",
                          u"모드: 새로 설치", u"Modo: instalación nueva"),
"An existing ClawTweaks installation was found. The setup will re-check all prerequisites and install the latest package.":
    (u"Eine vorhandene ClawTweaks-Installation wurde gefunden. Das Setup prüft alle Voraussetzungen erneut und installiert das neueste Paket.",
     u"Une installation ClawTweaks existante a été trouvée. Le setup revérifie les prérequis et installe le dernier paquet.",
     u"기존 ClawTweaks 설치를 찾았습니다. 설치 프로그램이 요구 사항을 다시 확인하고 최신 패키지를 설치합니다.",
     u"Se encontró una instalación de ClawTweaks. El setup revisa los requisitos e instala el último paquete."),
"No existing ClawTweaks installation was found. The setup will guide you through the full first-time installation.":
    (u"Keine vorhandene ClawTweaks-Installation gefunden. Das Setup führt dich durch die komplette Erstinstallation.",
     u"Aucune installation ClawTweaks trouvée. Le setup vous guide dans la première installation complète.",
     u"기존 ClawTweaks 설치가 없습니다. 설치 프로그램이 최초 설치 전체를 안내합니다.",
     u"No hay instalación previa de ClawTweaks. El setup te guía en la primera instalación."),
"Install ClawTweaks":    (u"ClawTweaks installieren", u"Installer ClawTweaks",
                          u"ClawTweaks 설치", u"Instalar ClawTweaks"),
"Signing certificate":   (u"Signaturzertifikat", u"Certificat de signature",
                          u"서명 인증서", u"Certificado de firma"),
"App package":           (u"App-Paket", u"Paquet de l'app", u"앱 패키지", u"Paquete de la app"),
"Required tools":        (u"Benötigte Tools", u"Outils requis", u"필요한 도구", u"Herramientas necesarias"),
"REBOOT REQUIRED":       (u"NEUSTART NÖTIG", u"REDÉMARRAGE REQUIS", u"재부팅 필요", u"REINICIO NECESARIO"),
"Not trusted yet — will be trusted during install.":
    (u"Noch nicht vertraut — wird bei der Installation eingetragen.",
     u"Pas encore approuvé — le sera pendant l'installation.",
     u"아직 신뢰되지 않음 — 설치 중에 등록됩니다.",
     u"Aún sin confianza — se añadirá durante la instalación."),
"Not running yet — starts after install via the Game Bar.":
    (u"Läuft noch nicht — startet nach der Installation über die Game Bar.",
     u"Pas encore lancé — démarre après l'installation via la Game Bar.",
     u"아직 실행 안 됨 — 설치 후 Game Bar에서 시작합니다.",
     u"Aún no activo — arranca tras instalar, desde la Game Bar."),
"Install ClawTweaks Center": (u"ClawTweaks Center installieren", u"Installer ClawTweaks Center",
                              u"ClawTweaks Center 설치", u"Instalar ClawTweaks Center"),
"Update ClawTweaks Center": (u"ClawTweaks Center aktualisieren", u"Mettre à jour ClawTweaks Center",
                             u"ClawTweaks Center 업데이트", u"Actualizar ClawTweaks Center"),
"ClawTweaks Center is already installed":
    (u"ClawTweaks Center ist bereits installiert", u"ClawTweaks Center est déjà installé",
     u"ClawTweaks Center가 이미 설치되어 있습니다", u"ClawTweaks Center ya está instalado"),
})

T.update(ONBOARDING)
T.update(INSTALLER)
T.update(PAD)
T.update(GAPS)
T.update(SETUP)
T.update(BROWSE)

LANGS = ["German", "French", "Korean", "Spanish"]
width, budget, esc = loc_gen.width, loc_gen.budget, loc_gen.esc

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

head = u'''using System.Collections.Generic;

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
        //
        // NOT IN HERE, and not by oversight: date format strings ("d MMM yyyy"), brand names, and
        // anything carrying a {placeholder}. An interpolated string is built at runtime, so it can
        // never match a key - those need the call site rewritten before a translation could show.

'''
body = []
for lang in LANGS:
    body.append(u"        private static readonly Dictionary<string, string> %s = new Dictionary<string, string>\r\n        {\r\n" % lang)
    for en, t in kept[lang]:
        body.append(u'            ["%s"] = "%s",\r\n' % (esc(en), esc(t)))
    body.append(u"        };\r\n\r\n")

note = [u"\r\n/*\r\n * LEFT IN ENGLISH ON PURPOSE - the honest translation is wider than the control it has to\r\n",
        u" * fit in (see the width rule above). This list is the answer to \"why is this one word\r\n",
        u" * still English\", so it is kept rather than tidied away:\r\n *\r\n"]
for lang, en, t, w, b in sorted(dropped):
    note.append(u' *   %-8s "%s" -> "%s" (%d wide, budget %d)\r\n' % (lang + ":", en, t, w, b))
note.append(u" */\r\n")

src = head + u''.join(body) + u"    }\r\n}\r\n" + (u''.join(note) if dropped else u'')
io.open('ClawTweaksCenter/Core/Localization.Tables.cs', 'w', encoding='utf-8-sig', newline='').write(src)

sys.stdout.write("\nentries per language: %s\n" % str(dict((l, len(kept[l])) for l in LANGS)))
sys.stdout.write("dropped: %d\n" % len(dropped))
for d in sorted(dropped):
    sys.stdout.write("   %-7s %-52s w=%d budget=%d\n" % (d[0], d[1][:52], d[3], d[4]))
