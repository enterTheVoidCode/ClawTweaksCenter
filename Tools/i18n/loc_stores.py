# -*- coding: utf-8 -*-
"""Round nine: the Other Stores shelf, and Misc becoming My Apps.

The store NAMES are absent on purpose. Ubisoft, EA, Battle.net and GOG are brands - they read the
same in all four languages, and a "translation" of one would be a rename.

"Misc" and "No tools added yet." from the earlier rounds are now unreachable keys: the shelf is
called My Apps and its empty line talks about apps. They are left in their round files rather than
deleted - an unreachable key costs a dictionary entry and nothing else, and removing one is how a
string that turned out to still be reachable somewhere quietly loses its translation.
"""

STORES = {

# ---- the new shelf -------------------------------------------------------
"Other Stores":  (u"Andere Stores", u"Autres stores", u"기타 스토어", u"Otras tiendas"),
"No games from these stores installed.":
    (u"Keine Spiele aus diesen Stores installiert.",
     u"Aucun jeu de ces stores n'est installé.",
     u"이 스토어의 게임이 설치되어 있지 않습니다.",
     u"No hay juegos instalados de estas tiendas."),

# ---- Misc -> My Apps -----------------------------------------------------
# The old name said what the shelf was NOT. This one says who put the things there, which is the
# only thing that separates it from the store tabs beside it.
"My Apps":       (u"Meine Apps", u"Mes apps", u"내 앱", u"Mis apps"),
"Add an app":    (u"App hinzufügen", u"Ajouter une app", u"앱 추가", u"Añadir una app"),
"No apps added yet.":
    (u"Noch keine Apps hinzugefügt.",
     u"Aucune app ajoutée.",
     u"아직 추가된 앱이 없습니다.",
     u"Aún no hay apps añadidas."),
}
